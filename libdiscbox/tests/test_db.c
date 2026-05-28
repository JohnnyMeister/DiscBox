/**
 * test_db.c — Unit tests for the database module
 */

#include "db.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

#define TEST(name) do { printf("  %-40s", name); fflush(stdout); } while(0)
#define PASS()     puts("PASS")
#define FAIL(msg)  do { printf("FAIL — %s\n", msg); failures++; } while(0)

static int failures = 0;

static db_ctx_t ctx;

static void test_open(void) {
    TEST("db_open (in-memory)");
    /* Use an in-memory SQLite database for tests */
    if (db_open(&ctx, ":memory:") != 0) { FAIL(db_last_error(&ctx)); return; }
    PASS();
}

static void test_insert_folder(void) {
    TEST("db_insert folder");
    db_entry_t e = {0};
    e.parent_id    = -1;
    e.name         = "Photos";
    e.virtual_path = "/Photos";
    e.type         = DB_ENTRY_FOLDER;
    e.created_at   = 1700000000;
    e.modified_at  = 1700000000;

    int64_t id = 0;
    if (db_insert(&ctx, &e, &id) != 0) { FAIL(db_last_error(&ctx)); return; }
    if (id <= 0) { FAIL("expected positive ID"); return; }
    PASS();
}

static void test_insert_file(void) {
    TEST("db_insert file");
    /* Get folder id first */
    db_entry_t folder = {0};
    if (db_get_by_path(&ctx, "/Photos", &folder) != 0) { FAIL("folder not found"); return; }

    db_entry_t e = {0};
    e.parent_id          = folder.id;
    e.name               = "cat.jpg";
    e.virtual_path       = "/Photos/cat.jpg";
    e.type               = DB_ENTRY_FILE;
    e.size_bytes         = 512000;
    e.mime_type          = "image/jpeg";
    e.chunk_message_ids  = "[\"111222333\"]";
    e.chunk_urls         = "[\"https://cdn.discordapp.com/attachments/x/y/cat.jpg.chunk_0000\"]";
    e.thumbnail_message_id = "444555666";
    e.thumbnail_url      = "https://cdn.discordapp.com/attachments/x/y/thumb.jpg";
    e.encrypted          = 0;
    e.created_at         = 1700000100;
    e.modified_at        = 1700000100;
    db_entry_free(&folder);

    int64_t id = 0;
    if (db_insert(&ctx, &e, &id) != 0) { FAIL(db_last_error(&ctx)); return; }
    PASS();
}

static void test_get_by_path(void) {
    TEST("db_get_by_path");
    db_entry_t e = {0};
    if (db_get_by_path(&ctx, "/Photos/cat.jpg", &e) != 0) { FAIL("not found"); return; }
    if (strcmp(e.name, "cat.jpg") != 0)           { FAIL("wrong name"); db_entry_free(&e); return; }
    if (e.size_bytes != 512000)                   { FAIL("wrong size"); db_entry_free(&e); return; }
    if (strcmp(e.mime_type, "image/jpeg") != 0)   { FAIL("wrong mime"); db_entry_free(&e); return; }
    if (e.thumbnail_message_id == NULL)            { FAIL("no thumbnail"); db_entry_free(&e); return; }
    db_entry_free(&e);
    PASS();
}

static void test_list_children(void) {
    TEST("db_list_children");
    db_entry_t *entries = NULL;
    size_t count = 0;
    if (db_list_children(&ctx, "/Photos", &entries, &count) != 0) { FAIL("list failed"); return; }
    if (count != 1) { FAIL("expected 1 child"); db_free_entries(entries, count); return; }
    if (strcmp(entries[0].name, "cat.jpg") != 0) { FAIL("wrong entry"); db_free_entries(entries, count); return; }
    db_free_entries(entries, count);
    PASS();
}

static void test_update(void) {
    TEST("db_update");
    db_entry_t e = {0};
    if (db_get_by_path(&ctx, "/Photos/cat.jpg", &e) != 0) { FAIL("not found"); return; }

    free(e.name);
    e.name = strdup("cat_renamed.jpg");
    free(e.virtual_path);
    e.virtual_path = strdup("/Photos/cat_renamed.jpg");
    e.size_bytes = 999999;

    if (db_update(&ctx, &e) != 0) { FAIL("update failed"); db_entry_free(&e); return; }
    db_entry_free(&e);

    db_entry_t updated = {0};
    if (db_get_by_path(&ctx, "/Photos/cat_renamed.jpg", &updated) != 0) { FAIL("renamed path not found"); return; }
    if (updated.size_bytes != 999999) { FAIL("size not updated"); db_entry_free(&updated); return; }
    db_entry_free(&updated);
    PASS();
}

static void test_total_size(void) {
    TEST("db_total_size");
    int64_t total = db_total_size(&ctx);
    if (total != 999999) { FAIL("wrong total size"); return; }
    PASS();
}

static void test_delete_tree(void) {
    TEST("db_delete_tree recursive");

    db_entry_t photos = {0};
    if (db_get_by_path(&ctx, "/Photos", &photos) != 0) { FAIL("photos not found"); return; }

    db_entry_t vacation = {0};
    vacation.parent_id = photos.id;
    vacation.name = "Vacation";
    vacation.virtual_path = "/Photos/Vacation";
    vacation.type = DB_ENTRY_FOLDER;
    vacation.created_at = 1700000200;
    vacation.modified_at = 1700000200;

    int64_t vacation_id = 0;
    if (db_insert(&ctx, &vacation, &vacation_id) != 0) {
        db_entry_free(&photos);
        FAIL(db_last_error(&ctx));
        return;
    }

    db_entry_t nested = {0};
    nested.parent_id = vacation_id;
    nested.name = "nested.txt";
    nested.virtual_path = "/Photos/Vacation/nested.txt";
    nested.type = DB_ENTRY_FILE;
    nested.size_bytes = 42;
    nested.mime_type = "text/plain";
    nested.chunk_message_ids = "[\"777888999\"]";
    nested.created_at = 1700000300;
    nested.modified_at = 1700000300;

    if (db_insert(&ctx, &nested, NULL) != 0) {
        db_entry_free(&photos);
        FAIL(db_last_error(&ctx));
        return;
    }

    db_entry_t empty_folder = {0};
    empty_folder.parent_id = vacation_id;
    empty_folder.name = "Empty";
    empty_folder.virtual_path = "/Photos/Vacation/Empty";
    empty_folder.type = DB_ENTRY_FOLDER;
    empty_folder.created_at = 1700000400;
    empty_folder.modified_at = 1700000400;

    if (db_insert(&ctx, &empty_folder, NULL) != 0) {
        db_entry_free(&photos);
        FAIL(db_last_error(&ctx));
        return;
    }

    db_entry_t *files = NULL;
    size_t count = 0;
    if (db_get_all_files_under(&ctx, "/Photos/Vacation", &files, &count) != 0) {
        db_entry_free(&photos);
        FAIL(db_last_error(&ctx));
        return;
    }
    if (count != 1 || strcmp(files[0].name, "nested.txt") != 0) {
        db_free_entries(files, count);
        db_entry_free(&photos);
        FAIL("wrong recursive file list");
        return;
    }
    db_free_entries(files, count);

    if (db_delete_tree(&ctx, "/Photos/Vacation") != 0) {
        db_entry_free(&photos);
        FAIL(db_last_error(&ctx));
        return;
    }

    db_entry_t check = {0};
    if (db_get_by_path(&ctx, "/Photos/Vacation/nested.txt", &check) == 0) {
        db_entry_free(&check);
        db_entry_free(&photos);
        FAIL("nested file still exists");
        return;
    }
    if (db_get_by_path(&ctx, "/Photos/Vacation/Empty", &check) == 0) {
        db_entry_free(&check);
        db_entry_free(&photos);
        FAIL("nested folder still exists");
        return;
    }
    if (db_get_by_path(&ctx, "/Photos/cat_renamed.jpg", &check) != 0) {
        db_entry_free(&photos);
        FAIL("sibling file was deleted");
        return;
    }

    db_entry_free(&check);
    db_entry_free(&photos);
    PASS();
}

static void test_delete(void) {
    TEST("db_delete");
    db_entry_t e = {0};
    if (db_get_by_path(&ctx, "/Photos/cat_renamed.jpg", &e) != 0) { FAIL("not found"); return; }
    int64_t id = e.id;
    db_entry_free(&e);

    if (db_delete(&ctx, id) != 0) { FAIL("delete failed"); return; }

    db_entry_t check = {0};
    if (db_get_by_path(&ctx, "/Photos/cat_renamed.jpg", &check) == 0) {
        FAIL("entry still exists after delete");
        db_entry_free(&check);
        return;
    }
    PASS();
}

static void test_close(void) {
    TEST("db_close");
    db_close(&ctx);
    PASS();
}

int main(void) {
    printf("=== libdiscbox db tests ===\n\n");

    test_open();
    test_insert_folder();
    test_insert_file();
    test_get_by_path();
    test_list_children();
    test_update();
    test_total_size();
    test_delete_tree();
    test_delete();
    test_close();

    printf("\n");
    if (failures == 0)
        printf("All tests PASSED ✓\n");
    else
        printf("%d test(s) FAILED\n", failures);

    return failures > 0 ? 1 : 0;
}
