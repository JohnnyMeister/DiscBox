/**
 * libdiscbox — Use Discord as personal cloud storage
 *
 * Public API. This is the only header consumers of the library need.
 *
 * Typical usage:
 *
 *   discbox_ctx_t *ctx = discbox_init("https://discord.com/api/webhooks/...", "~/.discbox/db.sqlite", NULL);
 *   discbox_upload(ctx, "/home/user/photo.jpg", "/Photos/photo.jpg", my_progress_cb, NULL);
 *   discbox_free(ctx);
 */

#ifndef DISCBOX_H
#define DISCBOX_H

#include <stddef.h>
#include <stdint.h>
#include <time.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ─────────────────────────── Version ──────────────────────────── */

#define DISCBOX_VERSION_MAJOR 0
#define DISCBOX_VERSION_MINOR 1
#define DISCBOX_VERSION_PATCH 1
#define DISCBOX_VERSION_STRING "0.1.3"

/* ─────────────────────────── Error codes ──────────────────────── */

typedef enum {
    DISCBOX_OK              =  0,   /* Success */
    DISCBOX_ERR_ARGS        = -1,   /* Invalid arguments */
    DISCBOX_ERR_MEMORY      = -2,   /* Memory allocation failed */
    DISCBOX_ERR_IO          = -3,   /* File I/O error */
    DISCBOX_ERR_NETWORK     = -4,   /* HTTP / network error */
    DISCBOX_ERR_DISCORD     = -5,   /* Discord API returned an error */
    DISCBOX_ERR_RATE_LIMIT  = -6,   /* Rate limited and retry exhausted */
    DISCBOX_ERR_DB          = -7,   /* SQLite error */
    DISCBOX_ERR_NOT_FOUND   = -8,   /* Virtual path not found */
    DISCBOX_ERR_EXISTS      = -9,   /* Virtual path already exists */
    DISCBOX_ERR_CANCELLED   = -10,  /* Operation cancelled by user */
    DISCBOX_ERR_CRYPTO      = -11,  /* Encryption/decryption error */
} discbox_err_t;

/** Return a human-readable string for an error code. */
const char *discbox_strerror(discbox_err_t err);

/* ─────────────────────────── Types ─────────────────────────────── */

/** Opaque library context. One per webhook/drive. */
typedef struct discbox_ctx discbox_ctx_t;

/** Entry type — file or folder */
typedef enum {
    DISCBOX_ENTRY_FILE   = 0,
    DISCBOX_ENTRY_FOLDER = 1,
} discbox_entry_type_t;

/** A single entry returned by discbox_list() */
typedef struct {
    int64_t  id;                    /* Internal DB id */
    char    *name;                  /* Filename or folder name */
    char    *virtual_path;          /* Full virtual path e.g. "/Photos/img.jpg" */
    discbox_entry_type_t type;
    int64_t  size_bytes;            /* 0 for folders */
    char    *mime_type;             /* e.g. "image/jpeg"; NULL if unknown */
    char    *thumbnail_url;         /* CDN URL if available; NULL otherwise */
    time_t   created_at;
    time_t   modified_at;
    int      encrypted;             /* 1 if chunks are encrypted */
} discbox_entry_t;

/** Progress callback — return 0 to continue, non-zero to cancel */
typedef int (*discbox_progress_cb_t)(
    void       *userdata,
    const char *virtual_path,   /* Which file is being transferred */
    int64_t     bytes_done,     /* Bytes transferred so far */
    int64_t     bytes_total,    /* Total bytes (-1 if unknown) */
    int         chunk_index,    /* Current chunk (0-based) */
    int         chunk_count     /* Total chunks (-1 if unknown) */
);

/** Configuration passed to discbox_init() */
typedef struct {
    /**
     * Chunk size in bytes.
     * Default (0): 8 * 1024 * 1024 (8 MiB).
     * Must be <= 8 MiB.
     */
    size_t chunk_size;

    /**
     * Whether to encrypt file chunks before uploading.
     * Uses AES-256-GCM with a key derived from the webhook URL (SHA-256).
     * Default: 0 (disabled).
     */
    int encrypt;

    /**
     * Maximum number of upload retries per chunk on transient errors / rate limits.
     * Default: 5.
     */
    int max_retries;

    /**
     * Timeout in seconds for individual HTTP requests.
     * Default: 60.
     */
    long http_timeout_sec;

    /**
     * Optional: path to the libdiscbox log file.
     * NULL = no file logging (stderr only in debug builds).
     */
    const char *log_path;
} discbox_config_t;

/* ─────────────────────────── Init / Teardown ───────────────────── */

/**
 * Initialise a DiscBox context.
 *
 * @param webhook_url  Full Discord webhook URL.
 *                     e.g. "https://discord.com/api/webhooks/1234/abcd"
 * @param db_path      Path to the SQLite database file.
 *                     Will be created if it does not exist.
 *                     e.g. "/home/user/.discbox/drive1.sqlite"
 * @param config       Optional configuration. Pass NULL for defaults.
 * @return             Allocated context, or NULL on error.
 */
discbox_ctx_t *discbox_init(
    const char          *webhook_url,
    const char          *db_path,
    const discbox_config_t *config
);

/**
 * Convenience initializer for consumers that cannot easily marshal
 * discbox_config_t across an FFI boundary.
 */
discbox_ctx_t *discbox_init_with_options(
    const char          *webhook_url,
    const char          *db_path,
    int                  encrypt
);

/**
 * Free a context and release all resources.
 * Safe to call with NULL.
 */
void discbox_free(discbox_ctx_t *ctx);

/**
 * Get the last error message for a context (more detailed than the error code).
 */
const char *discbox_last_error(discbox_ctx_t *ctx);

/* ─────────────────────────── Virtual FS ────────────────────────── */

/**
 * Create a virtual folder.
 *
 * @param ctx           Library context.
 * @param virtual_path  Absolute path e.g. "/Photos/Holidays".
 *                      Parent folders are created automatically.
 * @return              DISCBOX_OK or error code.
 */
discbox_err_t discbox_mkdir(discbox_ctx_t *ctx, const char *virtual_path);

/**
 * List the contents of a virtual folder.
 *
 * @param ctx           Library context.
 * @param virtual_path  Folder to list. Use "/" for the root.
 * @param[out] entries  Allocated array of entries. Free with discbox_free_entries().
 * @param[out] count    Number of entries.
 * @return              DISCBOX_OK or error code.
 */
discbox_err_t discbox_list(
    discbox_ctx_t    *ctx,
    const char       *virtual_path,
    discbox_entry_t **entries,
    size_t           *count
);

/**
 * Free entries returned by discbox_list().
 */
void discbox_free_entries(discbox_entry_t *entries, size_t count);

/**
 * Get metadata for a single virtual path (file or folder).
 *
 * @param[out] entry  Filled on success. Free with discbox_free_entry().
 */
discbox_err_t discbox_stat(
    discbox_ctx_t  *ctx,
    const char     *virtual_path,
    discbox_entry_t *entry
);

/** Free fields inside an entry filled by discbox_stat(). */
void discbox_free_entry(discbox_entry_t *entry);

/**
 * Rename or move a file/folder.
 *
 * @param old_path  Current virtual path.
 * @param new_path  New virtual path (may be in a different folder).
 */
discbox_err_t discbox_rename(
    discbox_ctx_t *ctx,
    const char    *old_path,
    const char    *new_path
);

/**
 * Delete a virtual file or folder.
 * Deleting a folder also deletes all its contents from Discord and the DB.
 * Discord messages for each chunk are deleted via the webhook.
 *
 * @param virtual_path  Path to delete.
 */
discbox_err_t discbox_delete(discbox_ctx_t *ctx, const char *virtual_path);

discbox_err_t discbox_delete_with_progress(
    discbox_ctx_t        *ctx,
    const char           *virtual_path,
    discbox_progress_cb_t progress_cb,
    void                 *userdata
);

/* ─────────────────────────── Transfer ──────────────────────────── */

/**
 * Upload a local file to the virtual file system.
 *
 * Steps performed internally:
 *   1. Split the file into chunks.
 *   2. (Optional) Encrypt each chunk.
 *   3. For each chunk: POST to Discord webhook with retry/rate-limit handling.
 *   4. If the file is an image or video, generate a thumbnail and upload it.
 *   5. Save all metadata (message IDs, CDN URLs, thumbnail) to SQLite.
 *
 * @param ctx           Library context.
 * @param local_path    Absolute path of the local file to upload.
 * @param virtual_path  Destination path in the virtual FS e.g. "/Docs/report.pdf".
 *                      The parent folder must exist (create with discbox_mkdir).
 * @param progress_cb   Optional progress callback. NULL = no progress.
 * @param userdata      Passed as-is to progress_cb.
 * @return              DISCBOX_OK or error code.
 */
discbox_err_t discbox_upload(
    discbox_ctx_t        *ctx,
    const char           *local_path,
    const char           *virtual_path,
    discbox_progress_cb_t progress_cb,
    void                 *userdata
);

/**
 * Download a virtual file to a local path.
 *
 * Steps performed internally:
 *   1. Look up file metadata in SQLite.
 *   2. For each chunk: GET from Discord CDN (no proxy needed — we are native).
 *   3. (Optional) Decrypt each chunk.
 *   4. Reassemble chunks into the output file.
 *
 * @param ctx           Library context.
 * @param virtual_path  Source path in the virtual FS.
 * @param local_path    Destination local path for the downloaded file.
 * @param progress_cb   Optional progress callback. NULL = no progress.
 * @param userdata      Passed as-is to progress_cb.
 * @return              DISCBOX_OK or error code.
 */
discbox_err_t discbox_download(
    discbox_ctx_t        *ctx,
    const char           *virtual_path,
    const char           *local_path,
    discbox_progress_cb_t progress_cb,
    void                 *userdata
);

/**
 * Write a consistent standalone SQLite backup of the current drive database.
 */
discbox_err_t discbox_backup_database(
    discbox_ctx_t        *ctx,
    const char           *local_path
);

/**
 * Reconcile the local metadata database with the real Discord messages.
 */
discbox_err_t discbox_sync_remote_state(
    discbox_ctx_t *ctx,
    int            remove_empty_folders,
    int           *checked_files,
    int           *removed_files,
    int           *removed_folders
);

/* ─────────────────────────── Utilities ─────────────────────────── */

/**
 * Validate a webhook URL without uploading anything.
 * Makes a GET request to Discord and checks the response.
 *
 * @return  DISCBOX_OK if valid, DISCBOX_ERR_DISCORD if invalid.
 */
discbox_err_t discbox_validate_webhook(const char *webhook_url);

/**
 * Returns the total number of bytes stored across all files in this drive.
 * Folders are not counted.
 */
int64_t discbox_total_size(discbox_ctx_t *ctx);

#ifdef __cplusplus
}
#endif

#endif /* DISCBOX_H */
