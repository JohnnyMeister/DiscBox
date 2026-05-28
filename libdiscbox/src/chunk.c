/**
 * chunk.c — File splitting and reassembly implementation
 */

#include "chunk.h"

#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <sys/stat.h>

/* ── Utility ──────────────────────────────────────────────────── */

int64_t chunk_file_size(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) return -1;
    fseek(f, 0, SEEK_END);
    int64_t size = (int64_t)ftell(f);
    fclose(f);
    return size;
}

int chunk_count_for_size(int64_t file_size, size_t chunk_size) {
    if (file_size <= 0) return 0;
    if (chunk_size == 0) chunk_size = CHUNK_SIZE_DEFAULT;
    return (int)((file_size + (int64_t)chunk_size - 1) / (int64_t)chunk_size);
}

/* ── Reader ───────────────────────────────────────────────────── */

int chunk_reader_open(const char *path, size_t chunk_size, chunk_reader_t *reader) {
    if (!path || !reader) return -1;

    if (chunk_size == 0) chunk_size = CHUNK_SIZE_DEFAULT;
    if (chunk_size > CHUNK_SIZE_MAX) chunk_size = CHUNK_SIZE_MAX;

    int64_t fsize = chunk_file_size(path);
    if (fsize < 0) return -1;

    FILE *fp = fopen(path, "rb");
    if (!fp) return -1;

    reader->fp          = fp;
    reader->chunk_size  = chunk_size;
    reader->file_size   = fsize;
    reader->chunk_count = chunk_count_for_size(fsize, chunk_size);
    reader->current     = 0;

    /* Edge case: empty file = one zero-byte chunk */
    if (reader->chunk_count == 0) reader->chunk_count = 1;

    return 0;
}

int chunk_reader_next(chunk_reader_t *reader, chunk_t *chunk) {
    if (!reader || !chunk || !reader->fp) return -1;
    if (reader->current >= reader->chunk_count) return 0; /* EOF */

    /* Allocate buffer */
    uint8_t *buf = (uint8_t *)malloc(reader->chunk_size);
    if (!buf) return -1;

    size_t bytes_read = fread(buf, 1, reader->chunk_size, reader->fp);

    if (bytes_read == 0 && ferror(reader->fp)) {
        free(buf);
        return -1;
    }

    /* Shrink buffer to actual bytes read (last chunk is usually smaller) */
    if (bytes_read < reader->chunk_size) {
        uint8_t *shrunk = (uint8_t *)realloc(buf, bytes_read > 0 ? bytes_read : 1);
        if (shrunk) buf = shrunk;
    }

    chunk->data  = buf;
    chunk->size  = bytes_read;
    chunk->index = reader->current;
    chunk->total = reader->chunk_count;

    reader->current++;
    return 1;
}

void chunk_reader_close(chunk_reader_t *reader) {
    if (reader && reader->fp) {
        fclose(reader->fp);
        reader->fp = NULL;
    }
}

void chunk_free(chunk_t *chunk) {
    if (chunk && chunk->data) {
        free(chunk->data);
        chunk->data = NULL;
        chunk->size = 0;
    }
}

/* ── Writer ───────────────────────────────────────────────────── */

int chunk_writer_open(const char *path, chunk_writer_t *writer) {
    if (!path || !writer) return -1;

    FILE *fp = fopen(path, "wb");
    if (!fp) return -1;

    writer->fp         = fp;
    writer->next_index = 0;
    return 0;
}

int chunk_writer_write(chunk_writer_t *writer, const chunk_t *chunk) {
    if (!writer || !chunk || !writer->fp) return -1;

    /* Enforce ordering: chunks must arrive in sequence */
    if (chunk->index != writer->next_index) {
        /* Out-of-order chunk — caller must sort before writing */
        return -1;
    }

    if (chunk->size > 0) {
        size_t written = fwrite(chunk->data, 1, chunk->size, writer->fp);
        if (written != chunk->size) return -1;
    }

    writer->next_index++;
    return 0;
}

void chunk_writer_close(chunk_writer_t *writer) {
    if (writer && writer->fp) {
        fflush(writer->fp);
        fclose(writer->fp);
        writer->fp = NULL;
    }
}
