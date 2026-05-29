/**
 * db.c — SQLite metadata store implementation
 */

#include "db.h"
#include "../vendor/sqlite3.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

/* ── Schema ──────────────────────────────────────────────────── */

static const char *SCHEMA_SQL =
    "PRAGMA journal_mode = WAL;"
    "PRAGMA foreign_keys = ON;"

    "CREATE TABLE IF NOT EXISTS entries ("
    "  id                    INTEGER PRIMARY KEY AUTOINCREMENT,"
    "  parent_id             INTEGER DEFAULT -1,"
    "  name                  TEXT    NOT NULL,"
    "  virtual_path          TEXT    NOT NULL UNIQUE,"
    "  type                  INTEGER NOT NULL,"   /* 0=file, 1=folder */
    "  size_bytes            INTEGER DEFAULT 0,"
    "  mime_type             TEXT,"
    "  chunk_message_ids     TEXT,"               /* JSON array of strings */
    "  chunk_urls            TEXT,"               /* JSON array of strings */
    "  thumbnail_message_id  TEXT,"
    "  thumbnail_url         TEXT,"
    "  encrypted             INTEGER DEFAULT 0,"
    "  created_at            INTEGER NOT NULL,"
    "  modified_at           INTEGER NOT NULL"
    ");"

    "CREATE INDEX IF NOT EXISTS idx_entries_parent ON entries(parent_id);"
    "CREATE INDEX IF NOT EXISTS idx_entries_path   ON entries(virtual_path);";

/* ── Helpers ─────────────────────────────────────────────────── */

static void set_error(db_ctx_t *ctx, const char *fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    free(ctx->last_error);
    ctx->last_error = strdup(buf);
    fprintf(stderr, "[db] %s\n", buf);
}

static char *safe_strdup(const char *s) {
    if (!s) return NULL;
    return strdup(s);
}

/**
 * Fill a db_entry_t from the current row of a prepared statement.
 * Column order must match the SELECT in db_get_by_path / db_list_children.
 */
static void row_to_entry(sqlite3_stmt *stmt, db_entry_t *e) {
    memset(e, 0, sizeof(*e));
    e->id                   = sqlite3_column_int64(stmt, 0);
    e->parent_id            = sqlite3_column_int64(stmt, 1);
    e->name                 = safe_strdup((const char *)sqlite3_column_text(stmt, 2));
    e->virtual_path         = safe_strdup((const char *)sqlite3_column_text(stmt, 3));
    e->type                 = (db_entry_type_t)sqlite3_column_int(stmt, 4);
    e->size_bytes           = sqlite3_column_int64(stmt, 5);
    e->mime_type            = safe_strdup((const char *)sqlite3_column_text(stmt, 6));
    e->chunk_message_ids    = safe_strdup((const char *)sqlite3_column_text(stmt, 7));
    e->chunk_urls           = safe_strdup((const char *)sqlite3_column_text(stmt, 8));
    e->thumbnail_message_id = safe_strdup((const char *)sqlite3_column_text(stmt, 9));
    e->thumbnail_url        = safe_strdup((const char *)sqlite3_column_text(stmt, 10));
    e->encrypted            = sqlite3_column_int(stmt, 11);
    e->created_at           = (time_t)sqlite3_column_int64(stmt, 12);
    e->modified_at          = (time_t)sqlite3_column_int64(stmt, 13);
}

static const char *SELECT_COLS =
    "id, parent_id, name, virtual_path, type, size_bytes, mime_type, "
    "chunk_message_ids, chunk_urls, thumbnail_message_id, thumbnail_url, "
    "encrypted, created_at, modified_at";

/* ── Lifecycle ───────────────────────────────────────────────── */

int db_open(db_ctx_t *ctx, const char *db_path) {
    if (!ctx || !db_path) return -1;
    memset(ctx, 0, sizeof(*ctx));

    int rc = sqlite3_open(db_path, &ctx->db);
    if (rc != SQLITE_OK) {
        set_error(ctx, "sqlite3_open failed: %s", sqlite3_errmsg(ctx->db));
        return -1;
    }

    char *errmsg = NULL;
    rc = sqlite3_exec(ctx->db, SCHEMA_SQL, NULL, NULL, &errmsg);
    if (rc != SQLITE_OK) {
        set_error(ctx, "schema init failed: %s", errmsg);
        sqlite3_free(errmsg);
        return -1;
    }

    return 0;
}

void db_close(db_ctx_t *ctx) {
    if (!ctx) return;
    if (ctx->db) {
        sqlite3_close(ctx->db);
        ctx->db = NULL;
    }
    free(ctx->last_error);
    ctx->last_error = NULL;
}

const char *db_last_error(db_ctx_t *ctx) {
    if (!ctx || !ctx->last_error) return "no error";
    return ctx->last_error;
}

/* ── CRUD ────────────────────────────────────────────────────── */

int db_insert(db_ctx_t *ctx, const db_entry_t *e, int64_t *out_id) {
    if (!ctx || !e) return -1;

    const char *sql =
        "INSERT INTO entries "
        "(parent_id, name, virtual_path, type, size_bytes, mime_type, "
        " chunk_message_ids, chunk_urls, thumbnail_message_id, thumbnail_url, "
        " encrypted, created_at, modified_at) "
        "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?);";

    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) {
        set_error(ctx, "prepare insert: %s", sqlite3_errmsg(ctx->db));
        return -1;
    }

    time_t now = time(NULL);
    sqlite3_bind_int64(stmt, 1,  e->parent_id);
    sqlite3_bind_text (stmt, 2,  e->name,                 -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 3,  e->virtual_path,         -1, SQLITE_TRANSIENT);
    sqlite3_bind_int  (stmt, 4,  (int)e->type);
    sqlite3_bind_int64(stmt, 5,  e->size_bytes);
    sqlite3_bind_text (stmt, 6,  e->mime_type,            -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 7,  e->chunk_message_ids,    -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 8,  e->chunk_urls,           -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 9,  e->thumbnail_message_id, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 10, e->thumbnail_url,        -1, SQLITE_TRANSIENT);
    sqlite3_bind_int  (stmt, 11, e->encrypted);
    sqlite3_bind_int64(stmt, 12, (int64_t)(e->created_at  ? e->created_at  : now));
    sqlite3_bind_int64(stmt, 13, (int64_t)(e->modified_at ? e->modified_at : now));

    int rc = sqlite3_step(stmt);
    sqlite3_finalize(stmt);

    if (rc != SQLITE_DONE) {
        set_error(ctx, "insert failed: %s", sqlite3_errmsg(ctx->db));
        return -1;
    }

    if (out_id) *out_id = sqlite3_last_insert_rowid(ctx->db);
    return 0;
}

int db_get_by_path(db_ctx_t *ctx, const char *virtual_path, db_entry_t *entry) {
    if (!ctx || !virtual_path || !entry) return -1;

    char sql[256];
    snprintf(sql, sizeof(sql),
             "SELECT %s FROM entries WHERE virtual_path = ? LIMIT 1;", SELECT_COLS);

    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) return -1;

    sqlite3_bind_text(stmt, 1, virtual_path, -1, SQLITE_TRANSIENT);
    int rc = sqlite3_step(stmt);

    if (rc == SQLITE_ROW) {
        row_to_entry(stmt, entry);
        sqlite3_finalize(stmt);
        return 0;
    }

    sqlite3_finalize(stmt);
    set_error(ctx, "path not found: %s", virtual_path);
    return -1;
}

int db_get_by_id(db_ctx_t *ctx, int64_t id, db_entry_t *entry) {
    if (!ctx || !entry) return -1;

    char sql[256];
    snprintf(sql, sizeof(sql),
             "SELECT %s FROM entries WHERE id = ? LIMIT 1;", SELECT_COLS);

    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) return -1;

    sqlite3_bind_int64(stmt, 1, id);
    int rc = sqlite3_step(stmt);

    if (rc == SQLITE_ROW) {
        row_to_entry(stmt, entry);
        sqlite3_finalize(stmt);
        return 0;
    }

    sqlite3_finalize(stmt);
    return -1;
}

int db_list_children(
    db_ctx_t    *ctx,
    const char  *folder_path,
    db_entry_t **entries,
    size_t      *count)
{
    if (!ctx || !folder_path || !entries || !count) return -1;

    /* First get the parent's ID */
    db_entry_t parent = {0};
    int64_t parent_id = -1; /* root */

    if (strcmp(folder_path, "/") != 0) {
        if (db_get_by_path(ctx, folder_path, &parent) != 0) return -1;
        parent_id = parent.id;
        db_entry_free(&parent);
    }

    char sql[512];
    snprintf(sql, sizeof(sql),
             "SELECT %s FROM entries WHERE parent_id = ? "
             "ORDER BY type DESC, name ASC;", SELECT_COLS);
    /* type DESC: folders (1) before files (0) */

    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) {
        set_error(ctx, "list prepare: %s", sqlite3_errmsg(ctx->db));
        return -1;
    }

    sqlite3_bind_int64(stmt, 1, parent_id);

    /* Collect rows */
    size_t capacity = 16;
    size_t n        = 0;
    db_entry_t *arr = (db_entry_t *)malloc(capacity * sizeof(db_entry_t));
    if (!arr) { sqlite3_finalize(stmt); return -1; }

    while (sqlite3_step(stmt) == SQLITE_ROW) {
        if (n >= capacity) {
            capacity *= 2;
            db_entry_t *tmp = (db_entry_t *)realloc(arr, capacity * sizeof(db_entry_t));
            if (!tmp) { db_free_entries(arr, n); sqlite3_finalize(stmt); return -1; }
            arr = tmp;
        }
        row_to_entry(stmt, &arr[n]);
        n++;
    }

    sqlite3_finalize(stmt);
    *entries = arr;
    *count   = n;
    return 0;
}

int db_update(db_ctx_t *ctx, const db_entry_t *e) {
    if (!ctx || !e) return -1;

    const char *sql =
        "UPDATE entries SET "
        "parent_id=?, name=?, virtual_path=?, size_bytes=?, mime_type=?, "
        "chunk_message_ids=?, chunk_urls=?, thumbnail_message_id=?, thumbnail_url=?, "
        "encrypted=?, modified_at=? "
        "WHERE id=?;";

    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) return -1;

    sqlite3_bind_int64(stmt, 1,  e->parent_id);
    sqlite3_bind_text (stmt, 2,  e->name,                 -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 3,  e->virtual_path,         -1, SQLITE_TRANSIENT);
    sqlite3_bind_int64(stmt, 4,  e->size_bytes);
    sqlite3_bind_text (stmt, 5,  e->mime_type,            -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 6,  e->chunk_message_ids,    -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 7,  e->chunk_urls,           -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 8,  e->thumbnail_message_id, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 9,  e->thumbnail_url,        -1, SQLITE_TRANSIENT);
    sqlite3_bind_int  (stmt, 10, e->encrypted);
    sqlite3_bind_int64(stmt, 11, (int64_t)time(NULL));
    sqlite3_bind_int64(stmt, 12, e->id);

    int rc = sqlite3_step(stmt);
    sqlite3_finalize(stmt);
    if (rc != SQLITE_DONE) {
        set_error(ctx, "update failed: %s", sqlite3_errmsg(ctx->db));
        return -1;
    }
    return 0;
}

int db_update_descendant_paths(
    db_ctx_t   *ctx,
    int64_t     folder_id,
    const char *old_prefix,
    const char *new_prefix)
{
    if (!ctx || !old_prefix || !new_prefix) return -1;

    const char *sql =
        "WITH RECURSIVE subtree(id) AS ("
        "  SELECT id FROM entries WHERE parent_id = ?"
        "  UNION ALL"
        "  SELECT e.id FROM entries e JOIN subtree s ON e.parent_id = s.id"
        ") "
        "UPDATE entries SET "
        "  virtual_path = ? || substr(virtual_path, length(?) + 1),"
        "  modified_at = ? "
        "WHERE id IN (SELECT id FROM subtree);";

    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) {
        set_error(ctx, "update descendant paths prepare: %s", sqlite3_errmsg(ctx->db));
        return -1;
    }

    sqlite3_bind_int64(stmt, 1, folder_id);
    sqlite3_bind_text (stmt, 2, new_prefix, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 3, old_prefix, -1, SQLITE_TRANSIENT);
    sqlite3_bind_int64(stmt, 4, (int64_t)time(NULL));

    int rc = sqlite3_step(stmt);
    sqlite3_finalize(stmt);

    if (rc != SQLITE_DONE) {
        set_error(ctx, "update descendant paths failed: %s", sqlite3_errmsg(ctx->db));
        return -1;
    }

    return 0;
}

int db_update_urls(
    db_ctx_t   *ctx,
    int64_t     id,
    const char *chunk_urls_json,
    const char *thumbnail_url)
{
    if (!ctx) return -1;
    const char *sql =
        "UPDATE entries SET chunk_urls=?, thumbnail_url=?, modified_at=? WHERE id=?;";

    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) return -1;

    sqlite3_bind_text (stmt, 1, chunk_urls_json, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text (stmt, 2, thumbnail_url,   -1, SQLITE_TRANSIENT);
    sqlite3_bind_int64(stmt, 3, (int64_t)time(NULL));
    sqlite3_bind_int64(stmt, 4, id);

    int rc = sqlite3_step(stmt);
    sqlite3_finalize(stmt);
    return (rc == SQLITE_DONE) ? 0 : -1;
}

int db_delete(db_ctx_t *ctx, int64_t id) {
    if (!ctx) return -1;
    const char *sql = "DELETE FROM entries WHERE id = ?;";

    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) return -1;

    sqlite3_bind_int64(stmt, 1, id);
    int rc = sqlite3_step(stmt);
    sqlite3_finalize(stmt);
    return (rc == SQLITE_DONE) ? 0 : -1;
}

int db_delete_tree(db_ctx_t *ctx, const char *virtual_path) {
    if (!ctx || !virtual_path) return -1;

    sqlite3_stmt *stmt = NULL;
    int rc;

    if (strcmp(virtual_path, "/") == 0) {
        const char *sql = "DELETE FROM entries;";
        if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) {
            set_error(ctx, "delete tree prepare: %s", sqlite3_errmsg(ctx->db));
            return -1;
        }
    } else {
        const char *sql =
            "WITH RECURSIVE subtree(id) AS ("
            "  SELECT id FROM entries WHERE virtual_path = ?"
            "  UNION ALL"
            "  SELECT e.id FROM entries e JOIN subtree s ON e.parent_id = s.id"
            ") "
            "DELETE FROM entries WHERE id IN (SELECT id FROM subtree);";

        if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) {
            set_error(ctx, "delete tree prepare: %s", sqlite3_errmsg(ctx->db));
            return -1;
        }
        sqlite3_bind_text(stmt, 1, virtual_path, -1, SQLITE_TRANSIENT);
    }

    rc = sqlite3_step(stmt);
    sqlite3_finalize(stmt);

    if (rc != SQLITE_DONE) {
        set_error(ctx, "delete tree failed: %s", sqlite3_errmsg(ctx->db));
        return -1;
    }

    return 0;
}

int db_get_all_files_under(
    db_ctx_t    *ctx,
    const char  *folder_path,
    db_entry_t **entries,
    size_t      *count)
{
    if (!ctx || !folder_path || !entries || !count) return -1;

    sqlite3_stmt *stmt = NULL;
    char sql[1024];

    if (strcmp(folder_path, "/") == 0) {
        snprintf(sql, sizeof(sql),
                 "SELECT %s FROM entries WHERE type = 0;", SELECT_COLS);
        if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) {
            set_error(ctx, "get files prepare: %s", sqlite3_errmsg(ctx->db));
            return -1;
        }
    } else {
        snprintf(sql, sizeof(sql),
                 "WITH RECURSIVE subtree(id) AS ("
                 "  SELECT id FROM entries WHERE virtual_path = ?"
                 "  UNION ALL"
                 "  SELECT e.id FROM entries e JOIN subtree s ON e.parent_id = s.id"
                 ") "
                 "SELECT %s FROM entries WHERE type = 0 "
                 "AND id IN (SELECT id FROM subtree);", SELECT_COLS);
        if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) {
            set_error(ctx, "get files prepare: %s", sqlite3_errmsg(ctx->db));
            return -1;
        }
        sqlite3_bind_text(stmt, 1, folder_path, -1, SQLITE_TRANSIENT);
    }

    size_t capacity = 16, n = 0;
    db_entry_t *arr = (db_entry_t *)malloc(capacity * sizeof(db_entry_t));
    if (!arr) { sqlite3_finalize(stmt); return -1; }

    int step_rc;
    while ((step_rc = sqlite3_step(stmt)) == SQLITE_ROW) {
        if (n >= capacity) {
            capacity *= 2;
            db_entry_t *tmp = (db_entry_t *)realloc(arr, capacity * sizeof(db_entry_t));
            if (!tmp) { db_free_entries(arr, n); sqlite3_finalize(stmt); return -1; }
            arr = tmp;
        }
        row_to_entry(stmt, &arr[n]);
        n++;
    }

    if (step_rc != SQLITE_DONE) {
        set_error(ctx, "get files failed: %s", sqlite3_errmsg(ctx->db));
        db_free_entries(arr, n);
        sqlite3_finalize(stmt);
        return -1;
    }

    sqlite3_finalize(stmt);
    *entries = arr;
    *count   = n;
    return 0;
}

int64_t db_total_size(db_ctx_t *ctx) {
    if (!ctx) return 0;
    const char *sql = "SELECT COALESCE(SUM(size_bytes), 0) FROM entries WHERE type = 0;";
    sqlite3_stmt *stmt = NULL;
    if (sqlite3_prepare_v2(ctx->db, sql, -1, &stmt, NULL) != SQLITE_OK) return 0;
    int64_t total = 0;
    if (sqlite3_step(stmt) == SQLITE_ROW) total = sqlite3_column_int64(stmt, 0);
    sqlite3_finalize(stmt);
    return total;
}

int db_backup_to_file(db_ctx_t *ctx, const char *backup_path) {
    if (!ctx || !ctx->db || !backup_path) return -1;

    remove(backup_path);

    sqlite3 *backup_db = NULL;
    int rc = sqlite3_open(backup_path, &backup_db);
    if (rc != SQLITE_OK) {
        set_error(ctx, "backup open failed: %s",
                  backup_db ? sqlite3_errmsg(backup_db) : "unknown error");
        if (backup_db) sqlite3_close(backup_db);
        return -1;
    }

    sqlite3_backup *backup = sqlite3_backup_init(backup_db, "main", ctx->db, "main");
    if (!backup) {
        set_error(ctx, "backup init failed: %s", sqlite3_errmsg(backup_db));
        sqlite3_close(backup_db);
        return -1;
    }

    rc = sqlite3_backup_step(backup, -1);
    int finish_rc = sqlite3_backup_finish(backup);
    if (finish_rc != SQLITE_OK)
        rc = finish_rc;

    if (rc != SQLITE_DONE && rc != SQLITE_OK) {
        set_error(ctx, "backup failed: %s", sqlite3_errmsg(backup_db));
        sqlite3_close(backup_db);
        remove(backup_path);
        return -1;
    }

    rc = sqlite3_close(backup_db);
    if (rc != SQLITE_OK) {
        set_error(ctx, "backup close failed");
        remove(backup_path);
        return -1;
    }

    return 0;
}

/* ── Memory ──────────────────────────────────────────────────── */

void db_entry_free(db_entry_t *e) {
    if (!e) return;
    free(e->name);
    free(e->virtual_path);
    free(e->mime_type);
    free(e->chunk_message_ids);
    free(e->chunk_urls);
    free(e->thumbnail_message_id);
    free(e->thumbnail_url);
    memset(e, 0, sizeof(*e));
}

void db_free_entries(db_entry_t *entries, size_t count) {
    if (!entries) return;
    for (size_t i = 0; i < count; i++) db_entry_free(&entries[i]);
    free(entries);
}
