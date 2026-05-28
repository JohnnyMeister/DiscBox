/**
 * discord.c - Discord Webhook HTTP client implementation
 */

#include "discord.h"

#include <curl/curl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DISCORD_MAX_RATE_LIMIT_RETRIES 120

typedef struct {
  uint8_t *data;
  size_t size;
  size_t capacity;
} dyn_buf_t;

static int dyn_buf_init(dyn_buf_t *b) {
  b->data = (uint8_t *)malloc(4096);
  if (!b->data)
    return -1;
  b->size = 0;
  b->capacity = 4096;
  b->data[0] = '\0';
  return 0;
}

static void dyn_buf_free(dyn_buf_t *b) {
  free(b->data);
  b->data = NULL;
  b->size = 0;
  b->capacity = 0;
}

static size_t write_callback(char *ptr, size_t size, size_t nmemb,
                             void *userdata) {
  size_t bytes = size * nmemb;
  dyn_buf_t *buf = (dyn_buf_t *)userdata;

  if (buf->size + bytes + 1 > buf->capacity) {
    size_t new_cap = (buf->capacity + bytes) * 2;
    uint8_t *tmp = (uint8_t *)realloc(buf->data, new_cap);
    if (!tmp)
      return 0;
    buf->data = tmp;
    buf->capacity = new_cap;
  }

  memcpy(buf->data + buf->size, ptr, bytes);
  buf->size += bytes;
  buf->data[buf->size] = '\0';
  return bytes;
}

static size_t header_callback(char *ptr, size_t size, size_t nmemb,
                              void *userdata) {
  return write_callback(ptr, size, nmemb, userdata);
}

static void set_error(discord_client_t *client, const char *fmt, ...) {
  char buf[512];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(buf, sizeof(buf), fmt, ap);
  va_end(ap);

  free(client->last_error);
  client->last_error = strdup(buf);
  fprintf(stderr, "[discord] %s\n", buf);
}

static int has_query_param(const char *url, const char *param) {
  const char *query = strchr(url, '?');
  return query && strstr(query + 1, param);
}

static int build_upload_url(const char *webhook_url, char *out,
                            size_t out_size) {
  if (has_query_param(webhook_url, "wait=true")) {
    return snprintf(out, out_size, "%s", webhook_url) < (int)out_size ? 0 : -1;
  }

  const char *sep = strchr(webhook_url, '?') ? "&" : "?";
  return snprintf(out, out_size, "%s%swait=true", webhook_url, sep) <
                 (int)out_size
             ? 0
             : -1;
}

static int build_message_url(const char *webhook_url, const char *message_id,
                             char *out, size_t out_size) {
  const char *query = strchr(webhook_url, '?');
  size_t base_len = query ? (size_t)(query - webhook_url) : strlen(webhook_url);

  return snprintf(out, out_size, "%.*s/messages/%s", (int)base_len,
                  webhook_url, message_id) < (int)out_size
             ? 0
             : -1;
}

static int parse_json_string_value(const char *start, const char *limit,
                                   const char *key, char *out,
                                   size_t out_size) {
  const char *key_pos = strstr(start, key);
  if (!key_pos || (limit && key_pos >= limit))
    return -1;

  const char *colon = strchr(key_pos, ':');
  if (!colon || (limit && colon >= limit))
    return -1;

  const char *value = colon + 1;
  while (*value == ' ' || *value == '\t' || *value == '\r' || *value == '\n')
    value++;
  if (*value != '"')
    return -1;
  value++;

  size_t len = 0;
  while (value[len] && value[len] != '"' &&
         (!limit || value + len < limit) && len < out_size - 1) {
    out[len] = value[len];
    len++;
  }
  out[len] = '\0';

  return len > 0 ? 0 : -1;
}

static double parse_retry_after_body(const char *body) {
  if (!body)
    return -1.0;

  const char *key = strstr(body, "\"retry_after\"");
  if (!key)
    return -1.0;

  const char *colon = strchr(key, ':');
  if (!colon)
    return -1.0;

  return atof(colon + 1);
}

static double retry_after_from_response(discord_client_t *client,
                                        const char *body) {
  double retry_after = parse_retry_after_body(body);
  if (retry_after <= 0.0)
    retry_after = client->ratelimit.retry_after;
  if (retry_after <= 0.0)
    retry_after = client->ratelimit.reset_after;
  if (retry_after <= 0.0)
    retry_after = 1.0;
  return retry_after;
}

static int handle_429(discord_client_t *client, const char *operation,
                      const char *body, int *rate_limit_attempts) {
  (*rate_limit_attempts)++;
  if (*rate_limit_attempts > DISCORD_MAX_RATE_LIMIT_RETRIES) {
    set_error(client, "%s rate limited too many times", operation);
    return -1;
  }

  double retry_after = retry_after_from_response(client, body);
  fprintf(stderr, "[discord] %s 429 - waiting %.2fs\n", operation,
          retry_after);
  ratelimit_handle_429(&client->ratelimit, retry_after);
  return 0;
}

static int parse_upload_response(const char *json,
                                 discord_upload_result_t *result) {
  memset(result, 0, sizeof(*result));

  const char *att = strstr(json, "\"attachments\":");
  if (!att)
    return -1;

  if (parse_json_string_value(json, att, "\"id\":", result->message_id,
                              sizeof(result->message_id)) != 0) {
    const char *att_end = strchr(att, ']');
    if (!att_end ||
        parse_json_string_value(att_end, NULL, "\"id\":", result->message_id,
                                sizeof(result->message_id)) != 0) {
      return -1;
    }
  }

  if (parse_json_string_value(att, NULL, "\"url\":", result->attachment_url,
                              sizeof(result->attachment_url)) != 0)
    return -1;

  return 0;
}

int discord_client_init(discord_client_t *client, long timeout_sec,
                        int max_retries) {
  if (!client)
    return -1;
  memset(client, 0, sizeof(*client));

  curl_global_init(CURL_GLOBAL_DEFAULT);

  CURL *curl = curl_easy_init();
  if (!curl)
    return -1;

  client->curl = curl;
  client->timeout_sec = (timeout_sec > 0) ? timeout_sec : 60;
  client->max_retries = (max_retries > 0) ? max_retries : 5;
  client->last_error = NULL;

  ratelimit_init(&client->ratelimit);
  return 0;
}

void discord_client_free(discord_client_t *client) {
  if (!client)
    return;
  if (client->curl) {
    curl_easy_cleanup((CURL *)client->curl);
    client->curl = NULL;
  }
  free(client->last_error);
  client->last_error = NULL;
}

const char *discord_client_last_error(discord_client_t *client) {
  if (!client || !client->last_error)
    return "no error";
  return client->last_error;
}

int discord_validate_webhook(discord_client_t *client,
                             const char *webhook_url) {
  if (!client || !webhook_url)
    return -1;

  CURL *curl = (CURL *)client->curl;
  dyn_buf_t body;
  if (dyn_buf_init(&body) != 0)
    return -1;

  curl_easy_reset(curl);
  curl_easy_setopt(curl, CURLOPT_CAINFO, "ca-bundle.crt");
  curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);
  curl_easy_setopt(curl, CURLOPT_URL, webhook_url);
  curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, write_callback);
  curl_easy_setopt(curl, CURLOPT_WRITEDATA, &body);
  curl_easy_setopt(curl, CURLOPT_TIMEOUT, client->timeout_sec);
  curl_easy_setopt(curl, CURLOPT_USERAGENT, "DiscBox/0.1");

  CURLcode res = curl_easy_perform(curl);
  long http_code = 0;
  curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_code);
  dyn_buf_free(&body);

  if (res != CURLE_OK) {
    set_error(client, "curl error: %s", curl_easy_strerror(res));
    return -1;
  }
  if (http_code != 200) {
    set_error(client, "webhook validation failed, HTTP %ld", http_code);
    return -1;
  }
  return 0;
}

int discord_upload_chunk(discord_client_t *client, const char *webhook_url,
                         const char *filename, const uint8_t *data,
                         size_t data_size, discord_upload_result_t *result) {
  if (!client || !webhook_url || !filename || !data || !result)
    return -1;

  char url[1024];
  if (build_upload_url(webhook_url, url, sizeof(url)) != 0) {
    set_error(client, "webhook upload URL is too long");
    return -1;
  }

  CURL *curl = (CURL *)client->curl;
  int attempt = 0;
  int rate_limit_attempts = 0;

  while (attempt <= client->max_retries) {
    ratelimit_wait_if_needed(&client->ratelimit);

    dyn_buf_t body, headers;
    if (dyn_buf_init(&body) != 0)
      return -1;
    if (dyn_buf_init(&headers) != 0) {
      dyn_buf_free(&body);
      return -1;
    }

    curl_mime *form = curl_mime_init(curl);
    if (!form) {
      dyn_buf_free(&body);
      dyn_buf_free(&headers);
      return -1;
    }

    curl_mimepart *part = curl_mime_addpart(form);
    curl_mime_name(part, "file");
    curl_mime_data(part, (const char *)data, data_size);
    curl_mime_filename(part, filename);
    curl_mime_type(part, "application/octet-stream");

    curl_easy_reset(curl);
    curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);
    curl_easy_setopt(curl, CURLOPT_URL, url);
    curl_easy_setopt(curl, CURLOPT_MIMEPOST, form);
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, write_callback);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &body);
    curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, header_callback);
    curl_easy_setopt(curl, CURLOPT_HEADERDATA, &headers);
    curl_easy_setopt(curl, CURLOPT_TIMEOUT, 0L);
    curl_easy_setopt(curl, CURLOPT_USERAGENT, "DiscBox/0.1");

    CURLcode res = curl_easy_perform(curl);
    long http_code = 0;
    curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_code);
    ratelimit_update(&client->ratelimit, (const char *)headers.data);
    curl_mime_free(form);

    if (res != CURLE_OK) {
      set_error(client, "curl error on upload: %s", curl_easy_strerror(res));
      dyn_buf_free(&body);
      dyn_buf_free(&headers);
      attempt++;
      continue;
    }

    if (http_code == 429) {
      if (handle_429(client, "upload", (const char *)body.data,
                     &rate_limit_attempts) != 0) {
        dyn_buf_free(&body);
        dyn_buf_free(&headers);
        return -1;
      }
      dyn_buf_free(&body);
      dyn_buf_free(&headers);
      continue;
    }

    if (http_code == 200) {
      int ok = parse_upload_response((const char *)body.data, result);
      dyn_buf_free(&body);
      dyn_buf_free(&headers);

      if (ok != 0) {
        set_error(client, "failed to parse Discord upload response");
        return -1;
      }
      return 0;
    }

    set_error(client, "Discord upload failed: HTTP %ld - %s", http_code,
              (char *)body.data);
    dyn_buf_free(&body);
    dyn_buf_free(&headers);

    if (http_code >= 400 && http_code < 500)
      return -1;

    attempt++;
  }

  set_error(client, "upload failed after %d attempts", client->max_retries);
  return -1;
}

int discord_fetch_message(discord_client_t *client, const char *webhook_url,
                          const char *message_id, uint8_t **out_data,
                          size_t *out_size) {
  if (!client || !webhook_url || !message_id || !out_data || !out_size)
    return -1;

  char url[768];
  if (build_message_url(webhook_url, message_id, url, sizeof(url)) != 0) {
    set_error(client, "message URL is too long");
    return -1;
  }

  CURL *curl = (CURL *)client->curl;
  int attempt = 0;
  int rate_limit_attempts = 0;

  while (attempt <= client->max_retries) {
    ratelimit_wait_if_needed(&client->ratelimit);

    dyn_buf_t body, headers;
    if (dyn_buf_init(&body) != 0)
      return -1;
    if (dyn_buf_init(&headers) != 0) {
      dyn_buf_free(&body);
      return -1;
    }

    curl_easy_reset(curl);
    curl_easy_setopt(curl, CURLOPT_FRESH_CONNECT, 1L);
    curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);
    curl_easy_setopt(curl, CURLOPT_TIMEOUT, 0L);
    curl_easy_setopt(curl, CURLOPT_URL, url);
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, write_callback);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &body);
    curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, header_callback);
    curl_easy_setopt(curl, CURLOPT_HEADERDATA, &headers);
    curl_easy_setopt(curl, CURLOPT_USERAGENT, "DiscBox/0.1");
    curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L);

    CURLcode res = curl_easy_perform(curl);
    long http_code = 0;
    curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_code);
    ratelimit_update(&client->ratelimit, (const char *)headers.data);

    if (res != CURLE_OK) {
      dyn_buf_free(&body);
      dyn_buf_free(&headers);
      attempt++;
      continue;
    }

    if (http_code == 429) {
      if (handle_429(client, "fetch_message", (const char *)body.data,
                     &rate_limit_attempts) != 0) {
        dyn_buf_free(&body);
        dyn_buf_free(&headers);
        return -1;
      }
      dyn_buf_free(&body);
      dyn_buf_free(&headers);
      continue;
    }

    if (http_code == 200) {
      dyn_buf_free(&headers);
      *out_data = body.data;
      *out_size = body.size;
      return 0;
    }

    set_error(client, "fetch message failed: HTTP %ld", http_code);
    dyn_buf_free(&body);
    dyn_buf_free(&headers);
    return -1;
  }

  set_error(client, "fetch message failed after %d attempts",
            client->max_retries);
  return -1;
}

int discord_download_url(discord_client_t *client, const char *cdn_url,
                         uint8_t **out_data, size_t *out_size) {
  if (!client || !cdn_url || !out_data || !out_size)
    return -1;

  CURL *curl = (CURL *)client->curl;
  dyn_buf_t body;
  if (dyn_buf_init(&body) != 0)
    return -1;

  curl_easy_reset(curl);
  curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);
  curl_easy_setopt(curl, CURLOPT_URL, cdn_url);
  curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, write_callback);
  curl_easy_setopt(curl, CURLOPT_WRITEDATA, &body);
  curl_easy_setopt(curl, CURLOPT_TIMEOUT, 0L);
  curl_easy_setopt(curl, CURLOPT_USERAGENT, "DiscBox/0.1");
  curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L);

  CURLcode res = curl_easy_perform(curl);
  long http_code = 0;
  curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_code);

  if (res != CURLE_OK || http_code != 200) {
    set_error(client, "download failed: %s (HTTP %ld)", curl_easy_strerror(res),
              http_code);
    dyn_buf_free(&body);
    return -1;
  }

  *out_data = body.data;
  *out_size = body.size;
  return 0;
}

int discord_delete_message(discord_client_t *client, const char *webhook_url,
                           const char *message_id) {
  if (!client || !webhook_url || !message_id)
    return -1;

  char url[768];
  if (build_message_url(webhook_url, message_id, url, sizeof(url)) != 0) {
    set_error(client, "message URL is too long");
    return -1;
  }

  CURL *curl = (CURL *)client->curl;
  int attempt = 0;
  int rate_limit_attempts = 0;

  while (attempt <= client->max_retries) {
    ratelimit_wait_if_needed(&client->ratelimit);

    dyn_buf_t body, headers;
    if (dyn_buf_init(&body) != 0)
      return -1;
    if (dyn_buf_init(&headers) != 0) {
      dyn_buf_free(&body);
      return -1;
    }

    curl_easy_reset(curl);
    curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);
    curl_easy_setopt(curl, CURLOPT_HTTPGET, 1L);
    curl_easy_setopt(curl, CURLOPT_POSTFIELDS, NULL);
    curl_easy_setopt(curl, CURLOPT_URL, url);
    curl_easy_setopt(curl, CURLOPT_CUSTOMREQUEST, "DELETE");
    curl_easy_setopt(curl, CURLOPT_TIMEOUT, 30L);
    curl_easy_setopt(curl, CURLOPT_USERAGENT, "DiscBox/0.1");
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, write_callback);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &body);
    curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, header_callback);
    curl_easy_setopt(curl, CURLOPT_HEADERDATA, &headers);

    CURLcode res = curl_easy_perform(curl);
    long http_code = 0;
    curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_code);
    ratelimit_update(&client->ratelimit, (const char *)headers.data);

    if (res != CURLE_OK) {
      set_error(client, "delete failed: %s", curl_easy_strerror(res));
      dyn_buf_free(&body);
      dyn_buf_free(&headers);
      attempt++;
      continue;
    }

    if (http_code == 429) {
      if (handle_429(client, "delete_message", (const char *)body.data,
                     &rate_limit_attempts) != 0) {
        dyn_buf_free(&body);
        dyn_buf_free(&headers);
        return -1;
      }
      dyn_buf_free(&body);
      dyn_buf_free(&headers);
      continue;
    }

    dyn_buf_free(&body);
    dyn_buf_free(&headers);

    if (http_code == 204 || http_code == 404)
      return 0;

    if (http_code >= 400 && http_code < 500) {
      set_error(client, "delete failed: HTTP %ld", http_code);
      return -1;
    }

    attempt++;
  }

  set_error(client, "delete failed after %d attempts", client->max_retries);
  return -1;
}
