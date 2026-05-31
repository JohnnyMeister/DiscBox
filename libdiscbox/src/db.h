/**
 * db.h — SQLite metadata store
 *
 * Manages the local SQLite database that holds all virtual filesystem
 * metadata: files, folders, chunk message IDs, thumbnail URLs, etc.
 *
 * The database is the only persistent state on the local machine.
 * Discord holds the actual file bytes; this DB maps the virtual path
 * hierarchy to Discord message IDs.
 */

#ifndef DISCBOX_DB_H
#define DISCBOX_DB_H

#include <stdint.h>
#include <stddef.h>
#include <time.h>

/* Forward declaration — we don't expose sqlite3.h in our public header */
typedef struct sqlite3 sqlite3;

/* ── Data structures ─────────────────────────────────────────── */

typedef enum {
    DB_ENTRY_FILE   = 0,
    DB_ENTRY_FOLDER = 1,
} db_entry_type_t;

/**
 * A file or folder record as stored in the database.
 * All char* fields are heap-allocated. Use db_entry_free() to release.
 */
typedef struct {
    int64_t          id;
    int64_t          parent_id;     /* -1 for root-level entries */
    char            *name;
    char            *virtual_path;  /* Full absolute path e.g. "/Photos/img.jpg" */
    db_entry_type_t  type;
    int64_t          size_bytes;
    char            *mime_type;     /* NULL if unknown */

    /* Serialised as JSON arrays, e.g. ["1234","5678"] */
    char            *chunk_message_ids;  /* NULL for folders */
    char            *chunk_urls;         /* NULL for folders; may be stale */

    char            *thumbnail_message_id; /* NULL if no thumbnail */
    char            *thumbnail_url;        /* NULL if no thumbnail; may be stale */

    int              encrypted;     /* 1 = chunks are AES-encrypted */
    time_t           created_at;
    time_t           modified_at;
} db_entry_t;

/* ── Context ─────────────────────────────────────────────────── */

typedef struct {
    sqlite3 *db;
    char    *last_error;
} db_ctx_t;

/* ── Lifecycle ───────────────────────────────────────────────── */

/**
 * Open (or create) the SQLite database at the given path.
 * Runs CREATE TABLE IF NOT EXISTS to set up the schema.
 *
 * @return  0 on success, -1 on error.
 */
int db_open(db_ctx_t *ctx, const char *db_path);

/** Close the database and release all resources. */
void db_close(db_ctx_t *ctx);

/** Get the last error message. */
const char *db_last_error(db_ctx_t *ctx);

/* ── CRUD operations ─────────────────────────────────────────── */

/**
 * Insert a new entry (file or folder) into the database.
 *
 * @param[out] out_id  The newly assigned row ID on success.
 * @return             0 on success, -1 on error.
 */
int db_insert(db_ctx_t *ctx, const db_entry_t *entry, int64_t *out_id);

/**
 * Retrieve an entry by its virtual path.
 *
 * @param[out] entry  Filled on success. Call db_entry_free() when done.
 * @return            0 on success, -1 on error or not found.
 */
int db_get_by_path(db_ctx_t *ctx, const char *virtual_path, db_entry_t *entry);

/**
 * Retrieve an entry by its database ID.
 */
int db_get_by_id(db_ctx_t *ctx, int64_t id, db_entry_t *entry);

/**
 * List all direct children of a folder (by the folder's virtual path).
 *
 * @param[out] entries  Allocated array. Free with db_free_entries().
 * @param[out] count    Number of entries.
 * @return              0 on success, -1 on error.
 */
int db_list_children(
    db_ctx_t    *ctx,
    const char  *folder_path,
    db_entry_t **entries,
    size_t      *count
);

/**
 * Update an existing entry's metadata (name, path, urls, etc.).
 * Identified by entry->id which must be valid.
 */
int db_update(db_ctx_t *ctx, const db_entry_t *entry);

/**
 * Update virtual paths for every descendant of a moved folder.
 */
int db_update_descendant_paths(
    db_ctx_t   *ctx,
    int64_t     folder_id,
    const char *old_prefix,
    const char *new_prefix
);

/**
 * Update the CDN URLs for a file's chunks (called after re-fetching from Discord).
 */
int db_update_urls(
    db_ctx_t   *ctx,
    int64_t     id,
    const char *chunk_urls_json,
    const char *thumbnail_url
);

/**
 * Delete an entry by ID.
 * Does NOT recursively delete children — callers must do that themselves.
 */
int db_delete(db_ctx_t *ctx, int64_t id);

/**
 * Delete an entry and every descendant below it.
 */
int db_delete_tree(db_ctx_t *ctx, const char *virtual_path);

/**
 * Delete every folder that no longer has children. Repeats until no more empty
 * folders remain, because deleting one folder can make its parent empty.
 */
int db_delete_empty_folders(db_ctx_t *ctx, int *out_deleted_count);

/**
 * Get all descendant file entries under a virtual folder path.
 * Used to enumerate everything that needs to be deleted from Discord
 * when a folder is removed.
 *
 * @param[out] entries  Allocated array of file entries only (no folders). Free with db_free_entries().
 */
int db_get_all_files_under(
    db_ctx_t    *ctx,
    const char  *folder_path,
    db_entry_t **entries,
    size_t      *count
);

/**
 * Get the total byte count of all files in the database.
 */
int64_t db_total_size(db_ctx_t *ctx);

/**
 * Copy the current SQLite database to a standalone backup file.
 * Uses sqlite3_backup so WAL pages are included consistently.
 */
int db_backup_to_file(db_ctx_t *ctx, const char *backup_path);

/* ── Memory management ───────────────────────────────────────── */

/** Release all heap-allocated fields inside an entry. Does NOT free the entry struct itself. */
void db_entry_free(db_entry_t *entry);

/** Free an array of entries returned by db_list_children() / db_get_all_files_under(). */
void db_free_entries(db_entry_t *entries, size_t count);

#endif /* DISCBOX_DB_H */
