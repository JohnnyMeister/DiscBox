/**
 * discbox_crypto.h - AES-256-GCM helpers for encrypted chunks.
 */

#ifndef DISCBOX_CRYPTO_H
#define DISCBOX_CRYPTO_H

#include <stddef.h>
#include <stdint.h>

#define DISCBOX_CRYPTO_HEADER_SIZE 36

int discbox_crypto_encrypt_chunk(const char *webhook_url, int chunk_index,
                                 const uint8_t *plain, size_t plain_size,
                                 uint8_t **out_data, size_t *out_size);

int discbox_crypto_decrypt_chunk(const char *webhook_url, int chunk_index,
                                 const uint8_t *encrypted,
                                 size_t encrypted_size,
                                 uint8_t **out_data, size_t *out_size);

#endif /* DISCBOX_CRYPTO_H */
