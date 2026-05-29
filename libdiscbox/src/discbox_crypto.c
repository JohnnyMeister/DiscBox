/**
 * discbox_crypto.c - AES-256-GCM chunk encryption.
 */

#include "discbox_crypto.h"

#include <openssl/crypto.h>
#include <openssl/evp.h>
#include <openssl/rand.h>

#include <stdlib.h>
#include <string.h>

static const uint8_t CRYPTO_MAGIC[8] = {'D', 'B', 'X', 'E',
                                        'N', 'C', '0', '1'};
static const size_t MAGIC_SIZE = 8;
static const size_t NONCE_SIZE = 12;
static const size_t TAG_SIZE = 16;

static int derive_key(const char *webhook_url, uint8_t key[32]) {
  if (!webhook_url)
    return -1;

  EVP_MD_CTX *md = EVP_MD_CTX_new();
  if (!md)
    return -1;

  int ok = EVP_DigestInit_ex(md, EVP_sha256(), NULL) == 1 &&
           EVP_DigestUpdate(md, webhook_url, strlen(webhook_url)) == 1 &&
           EVP_DigestFinal_ex(md, key, NULL) == 1;

  EVP_MD_CTX_free(md);
  return ok ? 0 : -1;
}

static void chunk_index_aad(int chunk_index, uint8_t aad[4]) {
  aad[0] = (uint8_t)(chunk_index & 0xff);
  aad[1] = (uint8_t)((chunk_index >> 8) & 0xff);
  aad[2] = (uint8_t)((chunk_index >> 16) & 0xff);
  aad[3] = (uint8_t)((chunk_index >> 24) & 0xff);
}

int discbox_crypto_encrypt_chunk(const char *webhook_url, int chunk_index,
                                 const uint8_t *plain, size_t plain_size,
                                 uint8_t **out_data, size_t *out_size) {
  if (!webhook_url || (!plain && plain_size > 0) || !out_data || !out_size)
    return -1;

  *out_data = NULL;
  *out_size = 0;

  uint8_t key[32];
  uint8_t nonce[12];
  uint8_t aad[4];
  if (derive_key(webhook_url, key) != 0)
    return -1;
  if (RAND_bytes(nonce, (int)sizeof(nonce)) != 1) {
    OPENSSL_cleanse(key, sizeof(key));
    return -1;
  }
  chunk_index_aad(chunk_index, aad);

  size_t total_size = DISCBOX_CRYPTO_HEADER_SIZE + plain_size;
  uint8_t *buf = (uint8_t *)malloc(total_size > 0 ? total_size : 1);
  if (!buf) {
    OPENSSL_cleanse(key, sizeof(key));
    return -1;
  }

  memcpy(buf, CRYPTO_MAGIC, MAGIC_SIZE);
  memcpy(buf + MAGIC_SIZE, nonce, NONCE_SIZE);

  EVP_CIPHER_CTX *ctx = EVP_CIPHER_CTX_new();
  if (!ctx) {
    free(buf);
    OPENSSL_cleanse(key, sizeof(key));
    return -1;
  }

  int len = 0;
  int ciphertext_len = 0;
  int ok = EVP_EncryptInit_ex(ctx, EVP_aes_256_gcm(), NULL, NULL, NULL) == 1 &&
           EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN,
                               (int)NONCE_SIZE, NULL) == 1 &&
           EVP_EncryptInit_ex(ctx, NULL, NULL, key, nonce) == 1 &&
           EVP_EncryptUpdate(ctx, NULL, &len, aad, sizeof(aad)) == 1;

  if (ok && plain_size > 0) {
    ok = EVP_EncryptUpdate(ctx, buf + DISCBOX_CRYPTO_HEADER_SIZE, &len,
                           plain, (int)plain_size) == 1;
    ciphertext_len = len;
  }

  if (ok) {
    ok = EVP_EncryptFinal_ex(ctx,
                             buf + DISCBOX_CRYPTO_HEADER_SIZE + ciphertext_len,
                             &len) == 1;
    ciphertext_len += len;
  }

  if (ok) {
    ok = EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_GET_TAG, (int)TAG_SIZE,
                             buf + MAGIC_SIZE + NONCE_SIZE) == 1;
  }

  EVP_CIPHER_CTX_free(ctx);
  OPENSSL_cleanse(key, sizeof(key));

  if (!ok || (size_t)ciphertext_len != plain_size) {
    free(buf);
    return -1;
  }

  *out_data = buf;
  *out_size = DISCBOX_CRYPTO_HEADER_SIZE + (size_t)ciphertext_len;
  return 0;
}

int discbox_crypto_decrypt_chunk(const char *webhook_url, int chunk_index,
                                 const uint8_t *encrypted,
                                 size_t encrypted_size,
                                 uint8_t **out_data, size_t *out_size) {
  if (!webhook_url || !encrypted || !out_data || !out_size)
    return -1;
  if (encrypted_size < DISCBOX_CRYPTO_HEADER_SIZE)
    return -1;
  if (memcmp(encrypted, CRYPTO_MAGIC, MAGIC_SIZE) != 0)
    return -1;

  *out_data = NULL;
  *out_size = 0;

  const uint8_t *nonce = encrypted + MAGIC_SIZE;
  const uint8_t *tag = encrypted + MAGIC_SIZE + NONCE_SIZE;
  const uint8_t *ciphertext = encrypted + DISCBOX_CRYPTO_HEADER_SIZE;
  size_t ciphertext_size = encrypted_size - DISCBOX_CRYPTO_HEADER_SIZE;

  uint8_t key[32];
  uint8_t aad[4];
  if (derive_key(webhook_url, key) != 0)
    return -1;
  chunk_index_aad(chunk_index, aad);

  uint8_t *buf = (uint8_t *)malloc(ciphertext_size > 0 ? ciphertext_size : 1);
  if (!buf) {
    OPENSSL_cleanse(key, sizeof(key));
    return -1;
  }

  EVP_CIPHER_CTX *ctx = EVP_CIPHER_CTX_new();
  if (!ctx) {
    free(buf);
    OPENSSL_cleanse(key, sizeof(key));
    return -1;
  }

  int len = 0;
  int plaintext_len = 0;
  int ok = EVP_DecryptInit_ex(ctx, EVP_aes_256_gcm(), NULL, NULL, NULL) == 1 &&
           EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN,
                               (int)NONCE_SIZE, NULL) == 1 &&
           EVP_DecryptInit_ex(ctx, NULL, NULL, key, nonce) == 1 &&
           EVP_DecryptUpdate(ctx, NULL, &len, aad, sizeof(aad)) == 1;

  if (ok && ciphertext_size > 0) {
    ok = EVP_DecryptUpdate(ctx, buf, &len, ciphertext,
                           (int)ciphertext_size) == 1;
    plaintext_len = len;
  }

  if (ok) {
    ok = EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_TAG, (int)TAG_SIZE,
                             (void *)tag) == 1;
  }

  if (ok) {
    ok = EVP_DecryptFinal_ex(ctx, buf + plaintext_len, &len) == 1;
    plaintext_len += len;
  }

  EVP_CIPHER_CTX_free(ctx);
  OPENSSL_cleanse(key, sizeof(key));

  if (!ok || (size_t)plaintext_len != ciphertext_size) {
    free(buf);
    return -1;
  }

  *out_data = buf;
  *out_size = (size_t)plaintext_len;
  return 0;
}
