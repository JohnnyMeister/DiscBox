/**
 * test_chunk.c — Unit tests for the chunk module
 *
 * Tests: split a file into chunks, write to separate temp files,
 * reassemble, verify byte-for-byte equality with the original.
 */

#include "chunk.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

#ifdef _WIN32
#  define TMPDIR "."
#else
#  define TMPDIR "/tmp"
#endif

#define TEST(name) do { printf("  %-40s", name); fflush(stdout); } while(0)
#define PASS()     puts("PASS")
#define FAIL(msg)  do { printf("FAIL — %s\n", msg); failures++; } while(0)

static int failures = 0;

/* Create a temp file filled with a repeating byte pattern */
static int make_test_file(const char *path, size_t size, uint8_t pattern) {
    FILE *f = fopen(path, "wb");
    if (!f) return -1;
    uint8_t buf[4096];
    for (size_t i = 0; i < sizeof(buf); i++) buf[i] = (uint8_t)(i & 0xFF ^ pattern);
    size_t written = 0;
    while (written < size) {
        size_t to_write = (size - written < sizeof(buf)) ? (size - written) : sizeof(buf);
        fwrite(buf, 1, to_write, f);
        written += to_write;
    }
    fclose(f);
    return 0;
}

/* Compare two files byte-by-byte */
static int files_equal(const char *a, const char *b) {
    FILE *fa = fopen(a, "rb");
    FILE *fb = fopen(b, "rb");
    if (!fa || !fb) { if(fa) fclose(fa); if(fb) fclose(fb); return 0; }
    int equal = 1;
    uint8_t ba[4096], bb[4096];
    while (1) {
        size_t ra = fread(ba, 1, sizeof(ba), fa);
        size_t rb = fread(bb, 1, sizeof(bb), fb);
        if (ra != rb || memcmp(ba, bb, ra) != 0) { equal = 0; break; }
        if (ra == 0) break;
    }
    fclose(fa); fclose(fb);
    return equal;
}

/* Test chunking then reassembly */
static void test_roundtrip(const char *label, size_t file_size, size_t chunk_size) {
    TEST(label);

    char src[256], dst[256];
    snprintf(src, sizeof(src), TMPDIR "/discbox_test_src_%zu.bin", file_size);
    snprintf(dst, sizeof(dst), TMPDIR "/discbox_test_dst_%zu.bin", file_size);

    if (make_test_file(src, file_size, 0xAB) != 0) { FAIL("make_test_file"); return; }

    /* Read chunks */
    chunk_reader_t reader;
    if (chunk_reader_open(src, chunk_size, &reader) != 0) { FAIL("reader open"); return; }

    int expected_chunks = chunk_count_for_size((int64_t)file_size, chunk_size);
    if (expected_chunks == 0) expected_chunks = 1;

    /* Write chunks into a reassembled output */
    chunk_writer_t writer;
    if (chunk_writer_open(dst, &writer) != 0) { FAIL("writer open"); chunk_reader_close(&reader); return; }

    chunk_t c;
    int chunk_idx = 0;
    while (1) {
        int got = chunk_reader_next(&reader, &c);
        if (got == 0) break;
        if (got < 0) { FAIL("reader_next"); chunk_writer_close(&writer); chunk_reader_close(&reader); return; }
        if (c.total != expected_chunks) { FAIL("wrong chunk count"); chunk_free(&c); break; }
        chunk_writer_write(&writer, &c);
        chunk_free(&c);
        chunk_idx++;
    }

    chunk_reader_close(&reader);
    chunk_writer_close(&writer);

    if (chunk_idx != expected_chunks) { FAIL("wrong number of chunks"); return; }

    /* Verify byte-for-byte equality */
    if (!files_equal(src, dst)) {
        remove(src);
        remove(dst);
        FAIL("file mismatch after reassembly");
        return;
    }

    remove(src);
    remove(dst);
    PASS();
}

static void test_chunk_count(void) {
    TEST("chunk_count_for_size");
    assert(chunk_count_for_size(0, 1024) == 0);
    assert(chunk_count_for_size(1, 1024) == 1);
    assert(chunk_count_for_size(1024, 1024) == 1);
    assert(chunk_count_for_size(1025, 1024) == 2);
    assert(chunk_count_for_size(2048, 1024) == 2);
    assert(chunk_count_for_size(2049, 1024) == 3);
    PASS();
}

int main(void) {
    printf("=== libdiscbox chunk tests ===\n\n");

    test_chunk_count();
    test_roundtrip("empty file (0 bytes)",           0,          4096);
    test_roundtrip("tiny file (< 1 chunk)",          100,        4096);
    test_roundtrip("exact 1 chunk boundary",         4096,       4096);
    test_roundtrip("1 chunk + 1 byte",               4097,       4096);
    test_roundtrip("multiple chunks (3 full)",       4096 * 3,   4096);
    test_roundtrip("multiple chunks + remainder",    10000,      4096);
    test_roundtrip("1 MB file, 256KB chunks",        1024*1024,  256*1024);
    test_roundtrip("5 MB file, 2MB chunks",          5*1024*1024, 2*1024*1024);

    printf("\n");
    if (failures == 0)
        printf("All tests PASSED ✓\n");
    else
        printf("%d test(s) FAILED\n", failures);

    return failures > 0 ? 1 : 0;
}
