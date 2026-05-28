/**
 * ratelimit.c — Discord rate limit tracking and backoff
 */

#include "ratelimit.h"

#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <time.h>

/* Portable sleep in milliseconds */
#ifdef _WIN32
#  include <windows.h>
#  define sleep_ms(ms) Sleep((DWORD)(ms))
#else
#  include <unistd.h>
#  define sleep_ms(ms) usleep((useconds_t)((ms) * 1000))
#endif

/* ── Helpers ─────────────────────────────────────────────────── */

/**
 * Case-insensitive search for a header value in a raw header block.
 * Returns the value string (pointing into headers), or NULL if not found.
 *
 * Example input:
 *   "HTTP/1.1 200 OK\r\nX-RateLimit-Remaining: 4\r\nContent-Type: …\r\n\r\n"
 */
static const char *find_header_value(const char *headers, const char *name) {
    if (!headers || !name) return NULL;

    size_t name_len = strlen(name);
    const char *p = headers;

    while (*p) {
        /* Skip to next line if this line doesn't start with our header name */
        /* Case-insensitive compare */
        int match = 1;
        for (size_t i = 0; i < name_len; i++) {
            char hc = p[i];
            char nc = name[i];
            /* toLower */
            if (hc >= 'A' && hc <= 'Z') hc += 32;
            if (nc >= 'A' && nc <= 'Z') nc += 32;
            if (hc != nc) { match = 0; break; }
        }

        if (match && p[name_len] == ':') {
            /* Found the header — skip ': ' and return pointer to value */
            const char *val = p + name_len + 1;
            while (*val == ' ') val++;
            return val;
        }

        /* Advance to next line */
        while (*p && *p != '\n') p++;
        if (*p == '\n') p++;
    }

    return NULL;
}

static double parse_double_header(const char *headers, const char *name) {
    const char *val = find_header_value(headers, name);
    if (!val) return -1.0;
    return atof(val);
}

static int parse_int_header(const char *headers, const char *name) {
    const char *val = find_header_value(headers, name);
    if (!val) return -1;
    return atoi(val);
}

/* ── Public API ──────────────────────────────────────────────── */

void ratelimit_init(ratelimit_t *rl) {
    if (!rl) return;
    rl->limit       = 5;    /* Discord's typical webhook bucket size */
    rl->remaining   = 5;
    rl->reset_after = 0.0;
    rl->retry_after = 1.0;
    rl->is_global   = 0;
}

void ratelimit_update(ratelimit_t *rl, const char *headers) {
    if (!rl || !headers) return;

    int limit = parse_int_header(headers, "X-RateLimit-Limit");
    if (limit >= 0) rl->limit = limit;

    int remaining = parse_int_header(headers, "X-RateLimit-Remaining");
    if (remaining >= 0) rl->remaining = remaining;

    double reset_after = parse_double_header(headers, "X-RateLimit-Reset-After");
    if (reset_after >= 0.0) rl->reset_after = reset_after;

    double retry_after = parse_double_header(headers, "Retry-After");
    if (retry_after < 0.0)
        retry_after = parse_double_header(headers, "X-RateLimit-Retry-After");
    if (retry_after >= 0.0) rl->retry_after = retry_after;

    const char *global_val = find_header_value(headers, "X-RateLimit-Global");
    rl->is_global = (global_val != NULL);

#ifdef DISCBOX_DEBUG
    fprintf(stderr, "[ratelimit] limit=%d remaining=%d reset_after=%.2fs\n",
            rl->limit, rl->remaining, rl->reset_after);
#endif
}

void ratelimit_wait_if_needed(ratelimit_t *rl) {
    if (!rl) return;

    if (rl->remaining <= 0 && rl->reset_after > 0.0) {
        /* Add a small safety margin (100ms) to avoid hitting the limit edge */
        double wait_sec = rl->reset_after + 0.1;
        fprintf(stderr, "[discbox] Rate limit bucket empty — waiting %.2fs\n", wait_sec);
        sleep_ms((long)(wait_sec * 1000.0));

        /* Reset after waiting */
        rl->remaining   = rl->limit;
        rl->reset_after = 0.0;
    }
}

void ratelimit_handle_429(ratelimit_t *rl, double retry_after) {
    if (!rl) return;

    if (retry_after <= 0.0) retry_after = rl->retry_after;
    if (retry_after <= 0.0) retry_after = 1.0; /* final fallback */

    fprintf(stderr, "[discbox] 429 Too Many Requests — retrying in %.2fs\n", retry_after);
    sleep_ms((long)(retry_after * 1000.0));

    rl->remaining   = rl->limit;
    rl->reset_after = 0.0;
}
