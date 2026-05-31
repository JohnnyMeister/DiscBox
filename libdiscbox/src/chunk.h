/**
 * chunk.h — File splitting and reassembly
 *
 * Internal module. Splits a file into fixed-size byte chunks for upload
 * and reassembles them on download. Does NOT handle I/O to Discord.
 */

#ifndef DISCBOX_CHUNK_H
#define DISCBOX_CHUNK_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

/* Keep chunks safely below Discord's webhook request limit.
 * Multipart overhead plus encryption metadata can push a 10 MiB chunk over
 * the limit, so use 8 MiB for reliable uploads of large files/videos.
 */
#define CHUNK_SIZE_DEFAULT  (8ULL * 1024 * 1024)
#define CHUNK_SIZE_MAX      (8ULL * 1024 * 1024)

/**
 * A single in-memory chunk ready to be uploaded.
 * The data pointer is owned by the chunk and freed by chunk_free().
 */
typedef struct {
    uint8_t *data;          /* Raw bytes of this chunk */
    size_t   size;          /* Number of bytes in data */
    int      index;         /* 0-based chunk index */
    int      total;         /* Total number of chunks for this file */
} chunk_t;

/**
 * Context used during a streaming read — lets us read one chunk at a time
 * without loading the whole file into memory.
 */
typedef struct {
    FILE   *fp;             /* Open file handle (owned by caller) */
    size_t  chunk_size;     /* Bytes per chunk */
    int64_t file_size;      /* Total file size in bytes */
    int     chunk_count;    /* Total number of chunks */
    int     current;        /* Next chunk index to read */
} chunk_reader_t;

/**
 * Open a chunk reader for a file.
 *
 * @param path        Local filesystem path to the file.
 * @param chunk_size  Max bytes per chunk (use CHUNK_SIZE_DEFAULT).
 * @param[out] reader Initialised reader. Call chunk_reader_close() when done.
 * @return            0 on success, -1 on error.
 */
int chunk_reader_open(const char *path, size_t chunk_size, chunk_reader_t *reader);

/**
 * Read the next chunk from an open reader.
 *
 * @param reader      Initialised reader.
 * @param[out] chunk  Filled on success. Call chunk_free() to release.
 * @return            1 if a chunk was read, 0 if EOF, -1 on error.
 */
int chunk_reader_next(chunk_reader_t *reader, chunk_t *chunk);

/** Close the reader and release its file handle. */
void chunk_reader_close(chunk_reader_t *reader);

/**
 * Release memory owned by a chunk (chunk.data).
 * Does NOT free the chunk_t struct itself.
 */
void chunk_free(chunk_t *chunk);

/* ── Writer (reassembly on download) ──────────────────────────── */

/**
 * Context for streaming chunk assembly into an output file.
 */
typedef struct {
    FILE *fp;           /* Open output file (owned by caller) */
    int   next_index;   /* Expected next chunk index (for order validation) */
} chunk_writer_t;

/**
 * Open a chunk writer that writes to the given output path.
 * Creates or truncates the file.
 *
 * @param[out] writer  Initialised writer. Call chunk_writer_close() when done.
 * @return             0 on success, -1 on error.
 */
int chunk_writer_open(const char *path, chunk_writer_t *writer);

/**
 * Write a chunk into the output file.
 * Chunks MUST be written in order (index 0, 1, 2, …).
 *
 * @return  0 on success, -1 on error (wrong order or I/O failure).
 */
int chunk_writer_write(chunk_writer_t *writer, const chunk_t *chunk);

/** Flush and close the output file. */
void chunk_writer_close(chunk_writer_t *writer);

/* ── Utility ──────────────────────────────────────────────────── */

/**
 * Calculate how many chunks a file of file_size bytes will produce
 * when split with chunk_size bytes per chunk.
 */
int chunk_count_for_size(int64_t file_size, size_t chunk_size);

/**
 * Get the size in bytes of a local file.
 * Returns -1 on error.
 */
int64_t chunk_file_size(const char *path);

#endif /* DISCBOX_CHUNK_H */
