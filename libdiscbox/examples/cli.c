/**
 * cli.c — Simple DiscBox CLI example
 *
 * Usage:
 *   discbox_cli <webhook_url> <db_path> <command> [args...]
 *
 * Commands:
 *   validate              — validate the webhook URL
 *   mkdir <path>          — create a virtual folder
 *   ls [path]             — list folder contents (default: /)
 *   upload <local> <path> — upload a local file
 *   download <path> <out> — download a virtual file
 *   rm <path>             — delete a file or folder
 *   mv <old> <new>        — rename/move a file or folder
 *   size                  — total bytes stored
 */

#include "discbox.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int progress(void *ud, const char *path, int64_t done, int64_t total,
                    int chunk, int chunks)
{
    (void)ud;
    int pct = (total > 0) ? (int)(done * 100 / total) : 0;
    printf("\r  %s  chunk %d/%d  %lld/%lld bytes  [%d%%]   ",
           path, chunk + 1, chunks, (long long)done, (long long)total, pct);
    fflush(stdout);
    return 0; /* continue */
}

static void print_entry(const discbox_entry_t *e, int index) {
    const char *type = (e->type == DISCBOX_ENTRY_FOLDER) ? "📁" : "📄";
    printf("  %s  %-40s  %10lld bytes  %s\n",
           type, e->name, (long long)e->size_bytes,
           e->thumbnail_url ? "[thumb]" : "");
}

int main(int argc, char *argv[]) {
    if (argc < 4) {
        fprintf(stderr,
            "Usage: %s <webhook_url> <db_path> <command> [args...]\n"
            "Commands: validate, mkdir, ls, upload, download, rm, mv, size\n",
            argv[0]);
        return 1;
    }

    const char *webhook = argv[1];
    const char *db_path = argv[2];
    const char *cmd     = argv[3];

    /* validate doesn't need a full context */
    if (strcmp(cmd, "validate") == 0) {
        printf("Validating webhook...\n");
        discbox_err_t err = discbox_validate_webhook(webhook);
        if (err == DISCBOX_OK)
            printf("✓ Webhook is valid!\n");
        else
            printf("✗ Webhook invalid: %s\n", discbox_strerror(err));
        return (err == DISCBOX_OK) ? 0 : 1;
    }

    /* All other commands need a context */
    discbox_ctx_t *ctx = discbox_init(webhook, db_path, NULL);
    if (!ctx) {
        fprintf(stderr, "Failed to initialise discbox\n");
        return 1;
    }

    discbox_err_t err = DISCBOX_OK;

    if (strcmp(cmd, "mkdir") == 0 && argc >= 5) {
        err = discbox_mkdir(ctx, argv[4]);
        if (err == DISCBOX_OK) printf("Created folder: %s\n", argv[4]);

    } else if (strcmp(cmd, "ls") == 0) {
        const char *path = (argc >= 5) ? argv[4] : "/";
        discbox_entry_t *entries = NULL;
        size_t count = 0;
        err = discbox_list(ctx, path, &entries, &count);
        if (err == DISCBOX_OK) {
            printf("Contents of %s (%zu items):\n", path, count);
            for (size_t i = 0; i < count; i++) print_entry(&entries[i], (int)i);
            discbox_free_entries(entries, count);
        }

    } else if (strcmp(cmd, "upload") == 0 && argc >= 6) {
        printf("Uploading %s → %s\n", argv[4], argv[5]);
        err = discbox_upload(ctx, argv[4], argv[5], progress, NULL);
        printf("\n");
        if (err == DISCBOX_OK) printf("✓ Upload complete\n");

    } else if (strcmp(cmd, "download") == 0 && argc >= 6) {
        printf("Downloading %s → %s\n", argv[4], argv[5]);
        err = discbox_download(ctx, argv[4], argv[5], progress, NULL);
        printf("\n");
        if (err == DISCBOX_OK) printf("✓ Download complete\n");

    } else if (strcmp(cmd, "rm") == 0 && argc >= 5) {
        err = discbox_delete(ctx, argv[4]);
        if (err == DISCBOX_OK) printf("Deleted: %s\n", argv[4]);

    } else if (strcmp(cmd, "mv") == 0 && argc >= 6) {
        err = discbox_rename(ctx, argv[4], argv[5]);
        if (err == DISCBOX_OK) printf("Moved: %s → %s\n", argv[4], argv[5]);

    } else if (strcmp(cmd, "size") == 0) {
        int64_t total = discbox_total_size(ctx);
        double mb = (double)total / (1024.0 * 1024.0);
        printf("Total stored: %.2f MB (%lld bytes)\n", mb, (long long)total);

    } else {
        fprintf(stderr, "Unknown command or missing arguments: %s\n", cmd);
        err = DISCBOX_ERR_ARGS;
    }

    if (err != DISCBOX_OK) {
        fprintf(stderr, "Error: %s — %s\n",
                discbox_strerror(err), discbox_last_error(ctx));
    }

    discbox_free(ctx);
    return (err == DISCBOX_OK) ? 0 : 1;
}
