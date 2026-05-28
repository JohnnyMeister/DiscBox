/**
 * ratelimit.h — Discord rate limit tracking and backoff
 *
 * Discord's webhook endpoints return headers telling us how many requests
 * remain and when the bucket resets. This module tracks that state and
 * provides a sleep function callers should use between requests.
 *
 * Discord rate limit headers:
 *   X-RateLimit-Limit       — requests allowed per window
 *   X-RateLimit-Remaining   — requests left in current window
 *   X-RateLimit-Reset-After — seconds until the bucket resets
 *   X-RateLimit-Retry-After — (on 429) seconds to wait before retrying
 */

#ifndef DISCBOX_RATELIMIT_H
#define DISCBOX_RATELIMIT_H

#include <stdint.h>

typedef struct {
    int     limit;           /* X-RateLimit-Limit */
    int     remaining;       /* X-RateLimit-Remaining */
    double  reset_after;     /* X-RateLimit-Reset-After (seconds, float) */
    double  retry_after;     /* X-RateLimit-Retry-After on 429 (seconds, float) */
    int     is_global;       /* X-RateLimit-Global header was present */
} ratelimit_t;

/**
 * Initialise a rate limit tracker to safe defaults.
 */
void ratelimit_init(ratelimit_t *rl);

/**
 * Update the tracker by parsing Discord's response headers.
 *
 * @param rl      Rate limit tracker to update.
 * @param headers Raw HTTP response headers as a single string (curl gives these).
 */
void ratelimit_update(ratelimit_t *rl, const char *headers);

/**
 * Should be called before each request.
 *
 * If the current remaining count is 0, this function sleeps until the bucket
 * resets. Always returns immediately if there are remaining requests.
 *
 * @param rl       Rate limit tracker.
 */
void ratelimit_wait_if_needed(ratelimit_t *rl);

/**
 * Called when a 429 Too Many Requests is received.
 * Sleeps for the retry_after duration and resets the remaining counter.
 *
 * @param rl          Rate limit tracker (must have been updated from headers).
 * @param retry_after Seconds to wait (pass rl->retry_after, or a fallback).
 */
void ratelimit_handle_429(ratelimit_t *rl, double retry_after);

#endif /* DISCBOX_RATELIMIT_H */
