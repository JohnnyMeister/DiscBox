/**
 * discbox.c — Main library implementation
 *
 * Implements the public API declared in discbox.h by orchestrating
 * the chunk, discord, and db modules.
 */

#include "../include/discbox.h"

#include "chunk.h"
#include "db.h"
#include "discord.h"
#include "discbox_crypto.h"

#ifdef _WIN32
#include <windows.h>
#endif

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* ── Context struct ──────────────────────────────────────────── */

struct discbox_ctx {
  char *webhook_url;
  discord_client_t discord;
  db_ctx_t db;
  discbox_config_t config;
  char *last_error;
};

/* ── Internal helpers ─────────────────────────────────────────── */

static void set_error(discbox_ctx_t *ctx, const char *fmt, ...) {
  char buf[512];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(buf, sizeof(buf), fmt, ap);
  va_end(ap);
  free(ctx->last_error);
  ctx->last_error = strdup(buf);
  fprintf(stderr, "[discbox] %s\n", buf);
}

/**
 * Build a JSON array string from an array of C strings.
 * e.g. {"a","b","c"} → ["a","b","c"]
 * Returns heap-allocated string. Caller must free().
 */
static char *build_json_array(const char **items, int count) {
  /* Estimate size: each item is at most 512 chars + 3 for [""] */
  size_t cap = (size_t)(count * 520 + 4);
  char *buf = (char *)malloc(cap);
  if (!buf)
    return NULL;

  size_t pos = 0;
  buf[pos++] = '[';
  for (int i = 0; i < count; i++) {
    if (i > 0)
      buf[pos++] = ',';
    buf[pos++] = '"';
    size_t len = strlen(items[i]);
    memcpy(buf + pos, items[i], len);
    pos += len;
    buf[pos++] = '"';
  }
  buf[pos++] = ']';
  buf[pos] = '\0';
  return buf;
}

static void free_string_array(char **items, int count) {
  if (!items)
    return;
  for (int i = 0; i < count; i++)
    free(items[i]);
  free(items);
}

static int parse_json_string_array(const char *json, char ***out_items,
                                   int *out_count) {
  if (!out_items || !out_count)
    return -1;

  *out_items = NULL;
  *out_count = 0;

  if (!json || !*json)
    return 0;

  const char *p = strchr(json, '[');
  if (!p)
    return -1;
  p++;

  int capacity = 8;
  int count = 0;
  char **items = (char **)calloc((size_t)capacity, sizeof(char *));
  if (!items)
    return -1;

  while (*p) {
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n' || *p == ',')
      p++;

    if (*p == ']') {
      *out_items = items;
      *out_count = count;
      return 0;
    }

    if (*p != '"') {
      free_string_array(items, count);
      return -1;
    }
    p++;

    const char *start = p;
    while (*p && *p != '"')
      p++;
    if (*p != '"') {
      free_string_array(items, count);
      return -1;
    }

    size_t len = (size_t)(p - start);
    char *item = (char *)malloc(len + 1);
    if (!item) {
      free_string_array(items, count);
      return -1;
    }
    memcpy(item, start, len);
    item[len] = '\0';

    if (count >= capacity) {
      capacity *= 2;
      char **tmp = (char **)realloc(items, (size_t)capacity * sizeof(char *));
      if (!tmp) {
        free(item);
        free_string_array(items, count);
        return -1;
      }
      items = tmp;
    }

    items[count++] = item;
    p++;
  }

  free_string_array(items, count);
  return -1;
}

static int count_remote_messages_for_file(const db_entry_t *entry) {
  if (!entry || entry->type != DB_ENTRY_FILE)
    return 0;

  int total = 0;
  if (entry->thumbnail_message_id && entry->thumbnail_message_id[0])
    total++;

  char **message_ids = NULL;
  int message_count = 0;
  if (parse_json_string_array(entry->chunk_message_ids, &message_ids,
                              &message_count) == 0) {
    for (int i = 0; i < message_count; i++) {
      if (message_ids[i] && message_ids[i][0])
        total++;
    }
    free_string_array(message_ids, message_count);
  }

  return total;
}

static discbox_err_t delete_remote_messages_for_file(
    discbox_ctx_t *ctx, const db_entry_t *entry,
    discbox_progress_cb_t progress_cb, void *userdata, int *deleted_count,
    int total_count) {
  if (!entry || entry->type != DB_ENTRY_FILE)
    return DISCBOX_OK;

  if (entry->thumbnail_message_id && entry->thumbnail_message_id[0]) {
    if (discord_delete_message(&ctx->discord, ctx->webhook_url,
                               entry->thumbnail_message_id) != 0) {
      set_error(ctx, "delete: failed to delete thumbnail for %s: %s",
                entry->virtual_path,
                discord_client_last_error(&ctx->discord));
      return DISCBOX_ERR_NETWORK;
    }

    if (deleted_count)
      (*deleted_count)++;
    if (progress_cb &&
        progress_cb(userdata, entry->virtual_path, deleted_count ? *deleted_count : 0,
                    total_count, deleted_count ? *deleted_count - 1 : 0,
                    total_count)) {
      return DISCBOX_ERR_CANCELLED;
    }
  }

  char **message_ids = NULL;
  int message_count = 0;
  if (parse_json_string_array(entry->chunk_message_ids, &message_ids,
                              &message_count) != 0) {
    set_error(ctx, "delete: invalid chunk message ids for %s",
              entry->virtual_path);
    return DISCBOX_ERR_DB;
  }

  for (int i = 0; i < message_count; i++) {
    if (!message_ids[i] || !message_ids[i][0])
      continue;

    if (discord_delete_message(&ctx->discord, ctx->webhook_url,
                               message_ids[i]) != 0) {
      set_error(ctx, "delete: failed to delete chunk %d/%d for %s: %s", i + 1,
                message_count, entry->virtual_path,
                discord_client_last_error(&ctx->discord));
      free_string_array(message_ids, message_count);
      return DISCBOX_ERR_NETWORK;
    }

    if (deleted_count)
      (*deleted_count)++;
    if (progress_cb &&
        progress_cb(userdata, entry->virtual_path, deleted_count ? *deleted_count : 0,
                    total_count, deleted_count ? *deleted_count - 1 : i,
                    total_count)) {
      free_string_array(message_ids, message_count);
      return DISCBOX_ERR_CANCELLED;
    }
  }

  free_string_array(message_ids, message_count);
  return DISCBOX_OK;
}

/**
 * Detect a MIME type from a filename extension.
 * Very basic — good enough for thumbnail generation decisions.
 */
static const char *mime_from_filename(const char *filename) {
  const char *ext = strrchr(filename, '.');
  if (!ext)
    return NULL;
  ext++; /* skip the dot */

  struct {
    const char *ext;
    const char *mime;
  } map[] = {{"jpg", "image/jpeg"},      {"jpeg", "image/jpeg"},
             {"png", "image/png"},       {"gif", "image/gif"},
             {"webp", "image/webp"},     {"bmp", "image/bmp"},
             {"mp4", "video/mp4"},       {"mkv", "video/x-matroska"},
             {"avi", "video/avi"},       {"mov", "video/quicktime"},
             {"webm", "video/webm"},     {"pdf", "application/pdf"},
             {"mp3", "audio/mpeg"},      {"flac", "audio/flac"},
             {"zip", "application/zip"}, {NULL, NULL}};

  for (int i = 0; map[i].ext; i++) {
    /* case-insensitive compare */
    const char *e = ext;
    const char *m = map[i].ext;
    int match = 1;
    while (*m) {
      char ec = *e, mc = *m;
      if (ec >= 'A' && ec <= 'Z')
        ec += 32;
      if (ec != mc) {
        match = 0;
        break;
      }
      e++;
      m++;
    }
    if (match && *e == '\0')
      return map[i].mime;
  }
  return "application/octet-stream";
}

/**
 * Extract just the filename from a path (e.g. "/foo/bar.txt" → "bar.txt").
 * Returns a pointer into path, not a new allocation.
 */
static const char *basename_of(const char *path) {
  const char *last = strrchr(path, '/');
  return last ? last + 1 : path;
}

static int is_same_or_child_path(const char *path, const char *possible_parent) {
  size_t parent_len = strlen(possible_parent);
  return strcmp(path, possible_parent) == 0 ||
         (strncmp(path, possible_parent, parent_len) == 0 &&
          path[parent_len] == '/');
}

static int is_valid_entry_path(const char *path) {
  if (!path || path[0] != '/' || path[1] == '\0')
    return 0;

  if (strstr(path, "//") != NULL)
    return 0;

  size_t len = strlen(path);
  return len == 1 || path[len - 1] != '/';
}

static int is_safe_filename_char(char c) {
  return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
         (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
}

static void build_chunk_filename(const char *fname, int chunk_index, char *out,
                                 size_t out_size) {
  if (!out || out_size == 0)
    return;

  char safe[180];
  size_t pos = 0;
  if (fname) {
    for (const unsigned char *p = (const unsigned char *)fname;
         *p && pos < sizeof(safe) - 1; p++) {
      safe[pos++] = is_safe_filename_char((char)*p) ? (char)*p : '_';
    }
  }

  while (pos > 0 && safe[pos - 1] == '_')
    pos--;
  if (pos == 0) {
    memcpy(safe, "file", 4);
    pos = 4;
  }
  safe[pos] = '\0';

  snprintf(out, out_size, "%s.chunk_%04d", safe, chunk_index);
}

/**
 * Get or create the parent_id for a virtual path.
 * e.g. for "/Photos/2024/img.jpg", ensures /Photos and /Photos/2024 exist
 * and returns the DB id of /Photos/2024.
 *
 * Returns -1 (root) for top-level paths like "/img.jpg".
 */
static int64_t ensure_parent(discbox_ctx_t *ctx, const char *virtual_path) {
  /* Find last slash */
  const char *last_slash = strrchr(virtual_path, '/');
  if (!last_slash || last_slash == virtual_path) {
    return -1; /* root-level */
  }

  /* Build parent path */
  size_t parent_len = (size_t)(last_slash - virtual_path);
  if (parent_len == 1)
    return -1;

  char *parent_path = (char *)malloc(parent_len + 1);
  if (!parent_path)
    return -2; /* error */
  strncpy(parent_path, virtual_path, parent_len);
  parent_path[parent_len] = '\0';

  db_entry_t parent_entry = {0};
  if (db_get_by_path(&ctx->db, parent_path, &parent_entry) == 0) {
    int64_t id = parent_entry.id;
    db_entry_free(&parent_entry);
    free(parent_path);
    return id;
  }

  /* Parent doesn't exist — create it recursively */
  discbox_mkdir(ctx, parent_path); /* best-effort */

  /* Now it should exist */
  if (db_get_by_path(&ctx->db, parent_path, &parent_entry) == 0) {
    int64_t id = parent_entry.id;
    db_entry_free(&parent_entry);
    free(parent_path);
    return id;
  }
  free(parent_path);

  return -2; /* error */
}

/* ── Init / Teardown ─────────────────────────────────────────── */

discbox_ctx_t *discbox_init(const char *webhook_url, const char *db_path,
                            const discbox_config_t *config) {
  if (!webhook_url || !db_path)
    return NULL;

  discbox_ctx_t *ctx = (discbox_ctx_t *)calloc(1, sizeof(discbox_ctx_t));
  if (!ctx)
    return NULL;

  /* Apply config or defaults */
  if (config) {
    ctx->config = *config;
  }
  if (ctx->config.chunk_size == 0)
    ctx->config.chunk_size = CHUNK_SIZE_DEFAULT;
  if (ctx->config.chunk_size > CHUNK_SIZE_MAX)
    ctx->config.chunk_size = CHUNK_SIZE_MAX;
  if (ctx->config.max_retries == 0)
    ctx->config.max_retries = 5;
  if (ctx->config.http_timeout_sec == 0)
    ctx->config.http_timeout_sec = 300; /* 5 minutos para ficheiros grandes */

  ctx->webhook_url = strdup(webhook_url);
  if (!ctx->webhook_url)
    goto fail;

  /* Initialise HTTP client */
  if (discord_client_init(&ctx->discord, ctx->config.http_timeout_sec,
                          ctx->config.max_retries) != 0) {
    goto fail;
  }

  /* Open database */
  if (db_open(&ctx->db, db_path) != 0) {
    goto fail;
  }

  return ctx;

fail:
  discbox_free(ctx);
  return NULL;
}

discbox_ctx_t *discbox_init_with_options(const char *webhook_url,
                                         const char *db_path, int encrypt) {
  discbox_config_t config = {0};
  config.encrypt = encrypt ? 1 : 0;
  return discbox_init(webhook_url, db_path, &config);
}

void discbox_free(discbox_ctx_t *ctx) {
  if (!ctx)
    return;
  free(ctx->webhook_url);
  discord_client_free(&ctx->discord);
  db_close(&ctx->db);
  free(ctx->last_error);
  free(ctx);
}

const char *discbox_last_error(discbox_ctx_t *ctx) {
  if (!ctx || !ctx->last_error)
    return "no error";
  return ctx->last_error;
}

/* ── Virtual FS ──────────────────────────────────────────────── */

discbox_err_t discbox_mkdir(discbox_ctx_t *ctx, const char *virtual_path) {
  if (!ctx || !is_valid_entry_path(virtual_path))
    return DISCBOX_ERR_ARGS;

  /* Check if already exists */
  db_entry_t existing = {0};
  if (db_get_by_path(&ctx->db, virtual_path, &existing) == 0) {
    db_entry_free(&existing);
    return DISCBOX_ERR_EXISTS;
  }

  int64_t parent_id = ensure_parent(ctx, virtual_path);
  if (parent_id == -2) {
    set_error(ctx, "failed to create parent folder for %s", virtual_path);
    return DISCBOX_ERR_IO;
  }

  db_entry_t entry = {0};
  entry.parent_id = parent_id;
  entry.name = (char *)basename_of(virtual_path);
  entry.virtual_path = (char *)virtual_path;
  entry.type = DB_ENTRY_FOLDER;
  entry.created_at = time(NULL);
  entry.modified_at = entry.created_at;

  int64_t new_id = 0;
  if (db_insert(&ctx->db, &entry, &new_id) != 0) {
    set_error(ctx, "db_insert mkdir failed: %s", db_last_error(&ctx->db));
    return DISCBOX_ERR_DB;
  }

  return DISCBOX_OK;
}

discbox_err_t discbox_list(discbox_ctx_t *ctx, const char *virtual_path,
                           discbox_entry_t **entries, size_t *count) {
  if (!ctx || !virtual_path || !entries || !count)
    return DISCBOX_ERR_ARGS;

  db_entry_t *db_entries = NULL;
  size_t db_count = 0;

  if (db_list_children(&ctx->db, virtual_path, &db_entries, &db_count) != 0) {
    set_error(ctx, "list failed: %s", db_last_error(&ctx->db));
    return DISCBOX_ERR_DB;
  }

  /* Convert db_entry_t[] → discbox_entry_t[] */
  discbox_entry_t *out =
      (discbox_entry_t *)calloc(db_count, sizeof(discbox_entry_t));
  if (!out && db_count > 0) {
    db_free_entries(db_entries, db_count);
    return DISCBOX_ERR_MEMORY;
  }

  for (size_t i = 0; i < db_count; i++) {
    db_entry_t *d = &db_entries[i];
    discbox_entry_t *e = &out[i];
    e->id = d->id;
    e->name = d->name ? strdup(d->name) : NULL;
    e->virtual_path = d->virtual_path ? strdup(d->virtual_path) : NULL;
    e->type = (discbox_entry_type_t)d->type;
    e->size_bytes = d->size_bytes;
    e->mime_type = d->mime_type ? strdup(d->mime_type) : NULL;
    e->thumbnail_url = d->thumbnail_url ? strdup(d->thumbnail_url) : NULL;
    e->created_at = d->created_at;
    e->modified_at = d->modified_at;
    e->encrypted = d->encrypted;
  }

  db_free_entries(db_entries, db_count);
  *entries = out;
  *count = db_count;
  return DISCBOX_OK;
}

void discbox_free_entries(discbox_entry_t *entries, size_t count) {
  if (!entries)
    return;
  for (size_t i = 0; i < count; i++) {
    discbox_entry_t *e = &entries[i];
    free(e->name);
    free(e->virtual_path);
    free(e->mime_type);
    free(e->thumbnail_url);
  }
  free(entries);
}

discbox_err_t discbox_stat(discbox_ctx_t *ctx, const char *virtual_path,
                           discbox_entry_t *entry) {
  if (!ctx || !virtual_path || !entry)
    return DISCBOX_ERR_ARGS;

  db_entry_t d = {0};
  if (db_get_by_path(&ctx->db, virtual_path, &d) != 0)
    return DISCBOX_ERR_NOT_FOUND;

  memset(entry, 0, sizeof(*entry));
  entry->id = d.id;
  entry->name = d.name ? strdup(d.name) : NULL;
  entry->virtual_path = d.virtual_path ? strdup(d.virtual_path) : NULL;
  entry->type = (discbox_entry_type_t)d.type;
  entry->size_bytes = d.size_bytes;
  entry->mime_type = d.mime_type ? strdup(d.mime_type) : NULL;
  entry->thumbnail_url = d.thumbnail_url ? strdup(d.thumbnail_url) : NULL;
  entry->created_at = d.created_at;
  entry->modified_at = d.modified_at;
  entry->encrypted = d.encrypted;
  db_entry_free(&d);
  return DISCBOX_OK;
}

void discbox_free_entry(discbox_entry_t *entry) {
  if (!entry)
    return;
  free(entry->name);
  free(entry->virtual_path);
  free(entry->mime_type);
  free(entry->thumbnail_url);
  memset(entry, 0, sizeof(*entry));
}

discbox_err_t discbox_rename(discbox_ctx_t *ctx, const char *old_path,
                             const char *new_path) {
  if (!ctx || !is_valid_entry_path(old_path) || !is_valid_entry_path(new_path))
    return DISCBOX_ERR_ARGS;

  db_entry_t entry = {0};
  if (db_get_by_path(&ctx->db, old_path, &entry) != 0) {
    set_error(ctx, "rename: source not found: %s", old_path);
    return DISCBOX_ERR_NOT_FOUND;
  }

  if (entry.type == DB_ENTRY_FOLDER && is_same_or_child_path(new_path, old_path)) {
    set_error(ctx, "rename: cannot move folder inside itself: %s -> %s",
              old_path, new_path);
    db_entry_free(&entry);
    return DISCBOX_ERR_ARGS;
  }

  db_entry_t existing = {0};
  if (db_get_by_path(&ctx->db, new_path, &existing) == 0) {
    int same_entry = existing.id == entry.id;
    db_entry_free(&existing);
    if (!same_entry) {
      set_error(ctx, "rename: path already exists: %s", new_path);
      db_entry_free(&entry);
      return DISCBOX_ERR_DB;
    }
  }

  int64_t new_parent_id = ensure_parent(ctx, new_path);
  if (new_parent_id == -2) {
    db_entry_free(&entry);
    return DISCBOX_ERR_IO;
  }

  char *old_prefix = strdup(entry.virtual_path);
  if (!old_prefix) {
    db_entry_free(&entry);
    return DISCBOX_ERR_IO;
  }

  free(entry.virtual_path);
  entry.virtual_path = strdup(new_path);
  free(entry.name);
  entry.name = strdup(basename_of(new_path));
  entry.parent_id = new_parent_id;
  entry.modified_at = time(NULL);

  int rc = db_update(&ctx->db, &entry);
  if (rc == 0 && entry.type == DB_ENTRY_FOLDER) {
    rc = db_update_descendant_paths(&ctx->db, entry.id, old_prefix, new_path);
  }
  free(old_prefix);
  db_entry_free(&entry);

  if (rc != 0) {
    set_error(ctx, "rename failed: %s", db_last_error(&ctx->db));
    return DISCBOX_ERR_DB;
  }

  return DISCBOX_OK;
}

discbox_err_t discbox_delete_with_progress(discbox_ctx_t *ctx,
                                           const char *virtual_path,
                                           discbox_progress_cb_t progress_cb,
                                           void *userdata) {
  if (!ctx || !virtual_path)
    return DISCBOX_ERR_ARGS;

  db_entry_t target = {0};
  if (db_get_by_path(&ctx->db, virtual_path, &target) != 0) {
    set_error(ctx, "delete: path not found: %s", virtual_path);
    return DISCBOX_ERR_NOT_FOUND;
  }

  db_entry_t *files = NULL;
  size_t file_count = 0;
  if (db_get_all_files_under(&ctx->db, virtual_path, &files, &file_count) !=
      0) {
    set_error(ctx, "delete: failed to enumerate files under %s: %s",
              virtual_path, db_last_error(&ctx->db));
    db_entry_free(&target);
    return DISCBOX_ERR_DB;
  }

  int total_messages = 0;
  for (size_t i = 0; i < file_count; i++)
    total_messages += count_remote_messages_for_file(&files[i]);

  int deleted_messages = 0;
  if (progress_cb &&
      progress_cb(userdata, virtual_path, 0, total_messages, 0,
                  total_messages)) {
    db_free_entries(files, file_count);
    db_entry_free(&target);
    return DISCBOX_ERR_CANCELLED;
  }

  for (size_t i = 0; i < file_count; i++) {
    discbox_err_t err = delete_remote_messages_for_file(
        ctx, &files[i], progress_cb, userdata, &deleted_messages,
        total_messages);
    if (err != DISCBOX_OK) {
      db_free_entries(files, file_count);
      db_entry_free(&target);
      return err;
    }
  }

  if (db_delete_tree(&ctx->db, virtual_path) != 0) {
    set_error(ctx, "delete: failed to remove DB entries under %s: %s",
              virtual_path, db_last_error(&ctx->db));
    db_free_entries(files, file_count);
    db_entry_free(&target);
    return DISCBOX_ERR_DB;
  }

  db_free_entries(files, file_count);
  db_entry_free(&target);

  return DISCBOX_OK;
}

discbox_err_t discbox_delete(discbox_ctx_t *ctx, const char *virtual_path) {
  return discbox_delete_with_progress(ctx, virtual_path, NULL, NULL);
}

discbox_err_t discbox_import_file(discbox_ctx_t *ctx, const char *virtual_path,
                                  const char *name, int64_t size_bytes,
                                  const char *chunk_message_ids_json) {
  if (!ctx || !is_valid_entry_path(virtual_path) || !name || !chunk_message_ids_json)
    return DISCBOX_ERR_ARGS;

  /* Check if already exists */
  db_entry_t existing = {0};
  if (db_get_by_path(&ctx->db, virtual_path, &existing) == 0) {
    db_entry_free(&existing);
    return DISCBOX_OK; /* already imported, skip */
  }

  int64_t parent_id = ensure_parent(ctx, virtual_path);

  const char *mime = mime_from_filename(name);

  db_entry_t entry = {0};
  entry.parent_id = parent_id;
  entry.name = (char *)name;
  entry.virtual_path = (char *)virtual_path;
  entry.type = DB_ENTRY_FILE;
  entry.size_bytes = size_bytes;
  entry.mime_type = (char *)mime;
  entry.chunk_message_ids = (char *)chunk_message_ids_json;
  entry.chunk_urls = NULL;
  entry.encrypted = 0;
  entry.created_at = time(NULL);
  entry.modified_at = time(NULL);

  int64_t new_id = 0;
  if (db_insert(&ctx->db, &entry, &new_id) != 0)
    return DISCBOX_ERR_DB;

  return DISCBOX_OK;
}
/* ── Upload ──────────────────────────────────────────────────── */

discbox_err_t discbox_upload(discbox_ctx_t *ctx, const char *local_path,
                             const char *virtual_path,
                             discbox_progress_cb_t progress_cb,
                             void *userdata) {
  if (!ctx || !local_path || !virtual_path)
    return DISCBOX_ERR_ARGS;
  if (!is_valid_entry_path(virtual_path))
    return DISCBOX_ERR_ARGS;

  db_entry_t existing = {0};
  if (db_get_by_path(&ctx->db, virtual_path, &existing) == 0) {
    db_entry_free(&existing);
    set_error(ctx, "upload: path already exists: %s", virtual_path);
    return DISCBOX_ERR_EXISTS;
  }

  /* Check file size */
  int64_t file_size = chunk_file_size(local_path);
  if (file_size < 0) {
    set_error(ctx, "cannot read file: %s", local_path);
    return DISCBOX_ERR_IO;
  }

  int chunk_count = chunk_count_for_size(file_size, ctx->config.chunk_size);
  if (chunk_count == 0)
    chunk_count = 1;
  const char *mime = mime_from_filename(local_path);
  const char *fname = basename_of(local_path);

  /* Ensure parent folder exists */
  int64_t parent_id = ensure_parent(ctx, virtual_path);
  if (parent_id == -2) {
    set_error(ctx, "cannot create parent folder for %s", virtual_path);
    return DISCBOX_ERR_IO;
  }

  /* Open chunk reader */
  chunk_reader_t reader;
  if (chunk_reader_open(local_path, ctx->config.chunk_size, &reader) != 0) {
    set_error(ctx, "cannot open file for chunking: %s", local_path);
    return DISCBOX_ERR_IO;
  }

  /* Arrays to store results for each chunk */
  char **message_ids = (char **)calloc(chunk_count, sizeof(char *));
  char **cdn_urls = (char **)calloc(chunk_count, sizeof(char *));
  if (!message_ids || !cdn_urls) {
    chunk_reader_close(&reader);
    free(message_ids);
    free(cdn_urls);
    return DISCBOX_ERR_MEMORY;
  }

  /* Upload each chunk */
  chunk_t chunk;
  int chunks_uploaded = 0;
  discbox_err_t err = DISCBOX_OK;

  while (1) {
    int got = chunk_reader_next(&reader, &chunk);
    if (got == 0)
      break; /* EOF */
    if (got < 0) {
      set_error(ctx, "error reading chunk %d", chunks_uploaded);
      err = DISCBOX_ERR_IO;
      break;
    }

    /* Build filename for this chunk: "filename.chunk_0002" */
    char chunk_filename[256];
    build_chunk_filename(fname, chunk.index, chunk_filename,
                         sizeof(chunk_filename));

    /* Progress callback */
    if (progress_cb) {
      int64_t done = (int64_t)chunk.index * (int64_t)ctx->config.chunk_size;
      int cancel = progress_cb(userdata, virtual_path, done, file_size,
                               chunk.index, chunk_count);
      if (cancel) {
        chunk_free(&chunk);
        err = DISCBOX_ERR_CANCELLED;
        break;
      }
    }

    uint8_t *upload_data = chunk.data;
    size_t upload_size = chunk.size;
    if (ctx->config.encrypt) {
      if (discbox_crypto_encrypt_chunk(ctx->webhook_url, chunk.index,
                                       chunk.data, chunk.size, &upload_data,
                                       &upload_size) != 0) {
        set_error(ctx, "failed to encrypt chunk %d", chunk.index);
        chunk_free(&chunk);
        err = DISCBOX_ERR_CRYPTO;
        break;
      }
    }

    discord_upload_result_t result;
    if (discord_upload_chunk(&ctx->discord, ctx->webhook_url, chunk_filename,
                             upload_data, upload_size, &result) != 0) {
      set_error(ctx, "failed to upload chunk %d: %s", chunk.index,
                discord_client_last_error(&ctx->discord));
      if (upload_data != chunk.data)
        free(upload_data);
      chunk_free(&chunk);
      err = DISCBOX_ERR_NETWORK;
      break;
    }
    if (upload_data != chunk.data)
      free(upload_data);

    message_ids[chunk.index] = strdup(result.message_id);
    cdn_urls[chunk.index] = strdup(result.attachment_url);
    chunks_uploaded++;

    chunk_free(&chunk);
  }

  chunk_reader_close(&reader);

  if (err != DISCBOX_OK)
    goto cleanup;

  /* Final progress: 100% */
  if (progress_cb) {
    progress_cb(userdata, virtual_path, file_size, file_size, chunk_count,
                chunk_count);
  }

  /* Build JSON arrays for storage */
  char *msg_ids_json =
      build_json_array((const char **)message_ids, chunk_count);
  char *cdn_urls_json = build_json_array((const char **)cdn_urls, chunk_count);

  /* Insert DB entry */
  db_entry_t db_entry = {0};
  db_entry.parent_id = parent_id;
  db_entry.name = (char *)basename_of(virtual_path);
  db_entry.virtual_path = (char *)virtual_path;
  db_entry.type = DB_ENTRY_FILE;
  db_entry.size_bytes = file_size;
  db_entry.mime_type = (char *)mime;
  db_entry.chunk_message_ids = msg_ids_json;
  db_entry.chunk_urls = cdn_urls_json;
  db_entry.encrypted = ctx->config.encrypt;
  db_entry.created_at = time(NULL);
  db_entry.modified_at = db_entry.created_at;

  int64_t new_id = 0;
  if (db_insert(&ctx->db, &db_entry, &new_id) != 0) {
    set_error(ctx, "db insert failed after upload: %s",
              db_last_error(&ctx->db));
    for (int i = 0; i < chunk_count; i++) {
      if (message_ids[i])
        discord_delete_message(&ctx->discord, ctx->webhook_url,
                               message_ids[i]);
    }
    err = DISCBOX_ERR_DB;
  }

  free(msg_ids_json);
  free(cdn_urls_json);

cleanup:
  for (int i = 0; i < chunk_count; i++) {
    free(message_ids[i]);
    free(cdn_urls[i]);
  }
  free(message_ids);
  free(cdn_urls);
  return err;
}

/* ── Download ────────────────────────────────────────────────── */

discbox_err_t discbox_download(discbox_ctx_t *ctx, const char *virtual_path,
                               const char *local_path,
                               discbox_progress_cb_t progress_cb,
                               void *userdata) {
  if (!ctx || !virtual_path || !local_path)
    return DISCBOX_ERR_ARGS;

  /* Look up file in DB */
  db_entry_t entry = {0};
  if (db_get_by_path(&ctx->db, virtual_path, &entry) != 0) {
    set_error(ctx, "file not found: %s", virtual_path);
    return DISCBOX_ERR_NOT_FOUND;
  }

  if (entry.type != DB_ENTRY_FILE) {
    db_entry_free(&entry);
    return DISCBOX_ERR_ARGS;
  }

  /* Always fetch fresh URLs from message IDs — CDN URLs expire */
  if (entry.chunk_message_ids) {
    fprintf(stderr, "[discbox] chunk_message_ids from DB: %s\n", entry.chunk_message_ids);
    free(entry.chunk_urls);
    entry.chunk_urls = NULL;
    
    char **fetched_urls = NULL;
    int url_count = 0;

    /* Parse JSON array of message IDs: ["id1", "id2", ...] */
    const char *bracket = strchr(entry.chunk_message_ids, '[');
    if (!bracket) {
      set_error(ctx, "invalid chunk_message_ids format for %s", virtual_path);
      db_entry_free(&entry);
      return DISCBOX_ERR_NOT_FOUND;
    }

    /* First pass: count IDs by counting commas + 1 */
    int id_count = 1;  /* At least one ID if array is non-empty */
    const char *tmp = bracket;
    while ((tmp = strchr(tmp, ',')) != NULL) {
      id_count++;
      tmp++;
    }
    
    /* Check if array is actually empty: [] */
    const char *close = strchr(bracket, ']');
    if (!close || close == bracket + 1) {
      set_error(ctx, "no message IDs for %s", virtual_path);
      db_entry_free(&entry);
      return DISCBOX_ERR_NOT_FOUND;
    }

    fprintf(stderr, "[discbox] counted %d message IDs\n", id_count);

    fetched_urls = (char **)calloc(id_count, sizeof(char *));
    if (!fetched_urls) {
      db_entry_free(&entry);
      return DISCBOX_ERR_MEMORY;
    }

    if (progress_cb) {
      int cancel = progress_cb(userdata, virtual_path, 0, -1, 0, id_count);
      if (cancel) {
        free(fetched_urls);
        db_entry_free(&entry);
        return DISCBOX_ERR_CANCELLED;
      }
    }

    /* Parse each ID from the array */
    const char *p = bracket + 1;  /* Skip '[' */
    while (*p && *p != ']' && url_count < id_count) {
      /* Skip whitespace and find opening quote */
      while (*p && (*p == ' ' || *p == ','))
        p++;
      
      if (*p == '"') {
        p++;  /* Skip opening quote */
        const char *end = strchr(p, '"');
        if (!end)
          break;
        
        size_t len = (size_t)(end - p);
        char msg_id[DISCORD_MSG_ID_LEN];
        if (len >= sizeof(msg_id)) {
          p = end + 1;
          continue;
        }

        strncpy(msg_id, p, len);
        msg_id[len] = '\0';
        p = end + 1;

        fprintf(stderr, "[discbox] fetching message ID: %s\n", msg_id);

        uint8_t *data = NULL;
        size_t size = 0;
        /* Pequeno delay entre fetches para evitar rate limit do Discord */
if (url_count > 0) {
#ifdef _WIN32
    Sleep(250);
#else
    usleep(250000);
#endif
}
        if (discord_fetch_message(&ctx->discord, ctx->webhook_url, msg_id, &data,
                                  &size) != 0) {
          fprintf(stderr, "[discbox] fetch failed for message: %s\n", msg_id);
          /* Clean up and fail — don't continue with missing chunks */
          for (int i = 0; i < url_count; i++)
            free(fetched_urls[i]);
          free(fetched_urls);
          db_entry_free(&entry);
          set_error(ctx, "failed to fetch chunk URL for message %s: %s",
                    msg_id, discord_client_last_error(&ctx->discord));
          return DISCBOX_ERR_NETWORK;
        }

        fprintf(stderr, "[discbox] fetched message data (size=%zu): %.200s\n", size, (char *)data);

        /* Parse attachment URL from message JSON */
        const char *att = strstr((char *)data, "\"attachments\":");
        int found = 0;
        if (att) {
          const char *url_key = strstr(att, "\"url\":");
          if (url_key) {
            url_key += 6;
            while (*url_key == ' ' || *url_key == '"')
              url_key++;
            size_t j = 0;
            while (url_key[j] && url_key[j] != '"' && j < DISCORD_URL_LEN - 1)
              j++;
            if (j > 0) {
              fetched_urls[url_count] = (char *)malloc(j + 1);
              strncpy(fetched_urls[url_count], url_key, j);
              fetched_urls[url_count][j] = '\0';
              url_count++;
              found = 1;
            }
          }
        }
        free(data);

        if (!found) {
          fprintf(stderr, "[discbox] no attachment URL in message %s\n", msg_id);
          for (int i = 0; i < url_count; i++)
            free(fetched_urls[i]);
          free(fetched_urls);
          db_entry_free(&entry);
          set_error(ctx, "no attachment URL in message %s", msg_id);
          return DISCBOX_ERR_NETWORK;
        }

        if (progress_cb) {
          int cancel = progress_cb(userdata, virtual_path, url_count, -1,
                                   url_count - 1, id_count);
          if (cancel) {
            for (int i = 0; i < url_count; i++)
              free(fetched_urls[i]);
            free(fetched_urls);
            db_entry_free(&entry);
            return DISCBOX_ERR_CANCELLED;
          }
        }
      }
    }

    if (url_count != id_count) {
      fprintf(stderr, "[discbox] expected %d URLs, got %d\n", id_count,
              url_count);
      for (int i = 0; i < url_count; i++)
        free(fetched_urls[i]);
      free(fetched_urls);
      db_entry_free(&entry);
      return DISCBOX_ERR_NETWORK;
    }

    /* Build chunk_urls JSON */
    entry.chunk_urls = build_json_array((const char **)fetched_urls, url_count);
    for (int i = 0; i < url_count; i++)
      free(fetched_urls[i]);
    free(fetched_urls);
  }

  if (!entry.chunk_urls) {
    set_error(ctx, "no chunk URLs available for %s", virtual_path);
    db_entry_free(&entry);
    return DISCBOX_ERR_NOT_FOUND;
  }

  /* Parse the chunk_urls JSON array */
  /* Extract all quoted strings from the JSON array */
  int capacity = 64;
  char **urls = (char **)malloc(capacity * sizeof(char *));
  int url_count = 0;

  const char *p = entry.chunk_urls;
  while ((p = strchr(p, '"')) != NULL) {
    p++;
    const char *end = strchr(p, '"');
    if (!end)
      break;
    size_t len = (size_t)(end - p);
    if (url_count >= capacity) {
      capacity *= 2;
      urls = (char **)realloc(urls, capacity * sizeof(char *));
    }
    urls[url_count] = (char *)malloc(len + 1);
    strncpy(urls[url_count], p, len);
    urls[url_count][len] = '\0';
    url_count++;
    p = end + 1;
  }

  /* Open chunk writer */
  chunk_writer_t writer;
  if (chunk_writer_open(local_path, &writer) != 0) {
    set_error(ctx, "cannot create output file: %s", local_path);
    db_entry_free(&entry);
    for (int i = 0; i < url_count; i++)
      free(urls[i]);
    free(urls);
    return DISCBOX_ERR_IO;
  }

  discbox_err_t err = DISCBOX_OK;
  int64_t bytes_done = 0;

  for (int i = 0; i < url_count; i++) {
    if (progress_cb) {
      int cancel = progress_cb(userdata, virtual_path, bytes_done,
                               entry.size_bytes, i, url_count);
      if (cancel) {
        err = DISCBOX_ERR_CANCELLED;
        break;
      }
    }

    uint8_t *data = NULL;
    size_t size = 0;

    if (discord_download_url(&ctx->discord, urls[i], &data, &size) != 0) {
      set_error(ctx, "failed to download chunk %d from %s", i, urls[i]);
      err = DISCBOX_ERR_NETWORK;
      break;
    }

    if (entry.encrypted) {
      uint8_t *plain = NULL;
      size_t plain_size = 0;
      if (discbox_crypto_decrypt_chunk(ctx->webhook_url, i, data, size,
                                       &plain, &plain_size) != 0) {
        set_error(ctx, "failed to decrypt chunk %d for %s", i, virtual_path);
        free(data);
        err = DISCBOX_ERR_CRYPTO;
        break;
      }
      free(data);
      data = plain;
      size = plain_size;
    }

    chunk_t chunk = {data, size, i, url_count};
    if (chunk_writer_write(&writer, &chunk) != 0) {
      set_error(ctx, "error writing chunk %d to disk", i);
      free(data);
      err = DISCBOX_ERR_IO;
      break;
    }

    bytes_done += (int64_t)size;
    free(data);
  }

  chunk_writer_close(&writer);

  if (progress_cb && err == DISCBOX_OK) {
    progress_cb(userdata, virtual_path, entry.size_bytes, entry.size_bytes,
                url_count, url_count);
  }

  for (int i = 0; i < url_count; i++)
    free(urls[i]);
  free(urls);
  db_entry_free(&entry);
  return err;
}

discbox_err_t discbox_backup_database(discbox_ctx_t *ctx,
                                      const char *local_path) {
  if (!ctx || !local_path)
    return DISCBOX_ERR_ARGS;

  if (db_backup_to_file(&ctx->db, local_path) != 0) {
    set_error(ctx, "database backup failed: %s", db_last_error(&ctx->db));
    return DISCBOX_ERR_DB;
  }

  return DISCBOX_OK;
}

/* ── Utilities ───────────────────────────────────────────────── */

static int discord_message_exists_for_sync(discbox_ctx_t *ctx,
                                           const char *message_id) {
  if (!message_id || !message_id[0])
    return 0;

  uint8_t *data = NULL;
  size_t size = 0;
  if (discord_fetch_message(&ctx->discord, ctx->webhook_url, message_id, &data,
                            &size) == 0) {
    free(data);
    return 1;
  }

  const char *error = discord_client_last_error(&ctx->discord);
  if (error && strstr(error, "HTTP 404") != NULL)
    return 0;

  set_error(ctx, "sync: failed to verify Discord message %s: %s", message_id,
            error ? error : "unknown error");
  return -1;
}

static int file_remote_messages_exist(discbox_ctx_t *ctx,
                                      const db_entry_t *entry) {
  if (!entry || entry->type != DB_ENTRY_FILE)
    return 1;

  if (entry->thumbnail_message_id && entry->thumbnail_message_id[0]) {
    int exists =
        discord_message_exists_for_sync(ctx, entry->thumbnail_message_id);
    if (exists <= 0)
      return exists;
  }

  char **message_ids = NULL;
  int message_count = 0;
  if (parse_json_string_array(entry->chunk_message_ids, &message_ids,
                              &message_count) != 0) {
    set_error(ctx, "sync: invalid chunk message ids for %s",
              entry->virtual_path);
    return -1;
  }

  if (message_count == 0) {
    free_string_array(message_ids, message_count);
    return 0;
  }

  for (int i = 0; i < message_count; i++) {
    int exists = discord_message_exists_for_sync(ctx, message_ids[i]);
    if (exists <= 0) {
      free_string_array(message_ids, message_count);
      return exists;
    }
  }

  free_string_array(message_ids, message_count);
  return 1;
}

discbox_err_t discbox_sync_remote_state(discbox_ctx_t *ctx,
                                        int remove_empty_folders,
                                        int *checked_files,
                                        int *removed_files,
                                        int *removed_folders) {
  if (!ctx)
    return DISCBOX_ERR_ARGS;

  if (checked_files)
    *checked_files = 0;
  if (removed_files)
    *removed_files = 0;
  if (removed_folders)
    *removed_folders = 0;

  db_entry_t *files = NULL;
  size_t file_count = 0;
  if (db_get_all_files_under(&ctx->db, "/", &files, &file_count) != 0) {
    set_error(ctx, "sync: failed to list local files: %s",
              db_last_error(&ctx->db));
    return DISCBOX_ERR_DB;
  }

  int local_checked = 0;
  int local_removed_files = 0;
  for (size_t i = 0; i < file_count; i++) {
    local_checked++;
    int exists = file_remote_messages_exist(ctx, &files[i]);
    if (exists < 0) {
      db_free_entries(files, file_count);
      return DISCBOX_ERR_NETWORK;
    }

    if (exists == 0) {
      if (db_delete_tree(&ctx->db, files[i].virtual_path) != 0) {
        set_error(ctx, "sync: failed to remove stale file %s: %s",
                  files[i].virtual_path, db_last_error(&ctx->db));
        db_free_entries(files, file_count);
        return DISCBOX_ERR_DB;
      }
      local_removed_files++;
    }
  }
  db_free_entries(files, file_count);

  int local_removed_folders = 0;
  if (remove_empty_folders) {
    if (db_delete_empty_folders(&ctx->db, &local_removed_folders) != 0) {
      set_error(ctx, "sync: failed to remove empty folders: %s",
                db_last_error(&ctx->db));
      return DISCBOX_ERR_DB;
    }
  }

  if (checked_files)
    *checked_files = local_checked;
  if (removed_files)
    *removed_files = local_removed_files;
  if (removed_folders)
    *removed_folders = local_removed_folders;

  return DISCBOX_OK;
}

discbox_err_t discbox_validate_webhook(const char *webhook_url) {
  if (!webhook_url)
    return DISCBOX_ERR_ARGS;

  discord_client_t client;
  if (discord_client_init(&client, 30, 1) != 0)
    return DISCBOX_ERR_NETWORK;

  int ok = discord_validate_webhook(&client, webhook_url);
  discord_client_free(&client);
  return (ok == 0) ? DISCBOX_OK : DISCBOX_ERR_DISCORD;
}

int64_t discbox_total_size(discbox_ctx_t *ctx) {
  if (!ctx)
    return 0;
  return db_total_size(&ctx->db);
}

const char *discbox_strerror(discbox_err_t err) {
  switch (err) {
  case DISCBOX_OK:
    return "success";
  case DISCBOX_ERR_ARGS:
    return "invalid arguments";
  case DISCBOX_ERR_MEMORY:
    return "out of memory";
  case DISCBOX_ERR_IO:
    return "file I/O error";
  case DISCBOX_ERR_NETWORK:
    return "network error";
  case DISCBOX_ERR_DISCORD:
    return "Discord API error";
  case DISCBOX_ERR_RATE_LIMIT:
    return "rate limited";
  case DISCBOX_ERR_DB:
    return "database error";
  case DISCBOX_ERR_NOT_FOUND:
    return "not found";
  case DISCBOX_ERR_EXISTS:
    return "already exists";
  case DISCBOX_ERR_CANCELLED:
    return "cancelled";
  case DISCBOX_ERR_CRYPTO:
    return "encryption error";
  default:
    return "unknown error";
  }
}
