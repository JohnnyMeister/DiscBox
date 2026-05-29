# DiscBox

DiscBox is an experimental open-source personal cloud drive that uses Discord webhooks as a remote storage backend.

It provides a modern desktop cloud storage experience built on top of a virtual filesystem and chunk-based storage architecture.

DiscBox combines:

* Native high-performance backend code (C)
* Modern Avalonia desktop UI
* Discord-based storage
* Local metadata indexing
* Chunk reconstruction
* Per-drive encryption support
* Multi-drive/webhook architecture

⚠️ DiscBox is still under active development and should currently be considered experimental software.

---

# Features

## Multi-Drive System

DiscBox now supports multiple independent drives/webhooks.

Each drive can have:

* Its own Discord webhook
* Independent SQLite database
* Independent encryption state
* Custom drive name

The Drive section now behaves similarly to Windows Explorer drives.

Features:

* Add new drives/webhooks
* Rename drives
* Switch between drives
* Per-drive encryption toggle
* Independent storage isolation

---

## File Explorer

* Modern desktop file explorer UI
* Folder navigation
* Breadcrumb navigation
* Context menu support
* File/folder icons
* Status messages and operation feedback
* Sorting-ready architecture

---

## File Operations

* Upload files
* Download files
* Delete files/folders
* Rename files/folders
* Move files between folders
* Create folders
* Copy virtual paths
* Cut / Copy / Paste support
* Context menu actions

---

## Transfer System

* Chunked uploads using Discord webhooks
* Stable chunk reconstruction system
* Automatic file reconstruction from chunks
* Fresh Discord CDN URL retrieval
* Automatic expired URL recovery
* Upload progress tracking
* Download progress tracking
* Delete progress tracking
* Transfer cancellation support
* Real-time transfer speed display
* ETA estimation
* Chunk-level progress reporting
* Large file support
* Background async operations

---

# Encryption System

DiscBox now supports real encrypted storage.

Encryption is implemented directly inside the native libdiscbox backend using:

* AES-256-GCM encryption
* Per-drive encryption state
* Automatic decrypt-on-download pipeline

## Encryption Features

* Encrypted uploads stored unreadable on Discord
* Transparent decryption during download
* Per-file encryption metadata
* Encryption toggle per drive
* Existing files preserve their original encryption state

## Drive Encryption Toggle

Each drive has its own encryption toggle:

* Open lock = uploads stored normally
* Closed lock = uploads encrypted

Changing a drive encryption state only affects future uploads for that drive.

---

# Storage System

* Virtual filesystem abstraction
* SQLite metadata indexing
* Discord message-based chunk storage
* Automatic chunk reconstruction
* Expired CDN URL recovery
* Chunk metadata management
* Background helper processes

---

# Current Status

DiscBox is now stable for core storage operations.

The upload, download, delete, and reconstruction systems are fully operational.

---

# Working Features

✅ Multi-drive/webhook support

✅ Per-drive encryption

✅ AES-256-GCM encrypted uploads

✅ Uploading files

✅ Downloading files

✅ Stable chunk reconstruction

✅ Large file support

✅ File deletion (Discord + database sync)

✅ Folder deletion

✅ Rename operations

✅ Context menu actions

✅ Transfer progress windows

✅ Speed + ETA tracking

✅ Virtual filesystem

✅ Automatic Discord CDN URL refresh

✅ Duplicate upload prevention

✅ Background delete helper process

✅ Multi-chunk transfer support

---

# Important Fixes & Improvements

## Download System Rewrite

The download system was fully rewritten and stabilized.

Improvements:

* Stable chunk reconstruction
* Expired CDN URL recovery
* Better rate-limit handling
* Large file support
* Chunk ordering fixes
* Fresh Discord attachment retrieval

---

## Discord Cleanup on Delete

Deleting files/folders now:

* Deletes Discord webhook messages/chunks
* Cleans metadata database
* Handles missing Discord messages safely
* Uses a dedicated helper process to avoid UI deadlocks

---

## Duplicate Upload Protection

DiscBox now:

* Detects duplicate virtual paths before upload
* Prevents unnecessary Discord uploads
* Automatically cleans orphaned chunks on failure

---

## Unicode Upload Fixes

Fixed Discord upload failures caused by:

* Non-ASCII filenames
* Unicode chunk attachment names

DiscBox now:

* Uses safe ASCII chunk names internally
* Preserves original filenames in metadata/UI

---

## Improved Progress UI

Transfer windows now display:

* Current chunk
* Total chunks
* Bytes processed
* Transfer speed
* ETA estimation
* URL retrieval phase
* Delete progress status

---

# Technical Details

## Chunk Size

Files are split into:

* 10MB chunks

This limit exists because of Discord webhook upload restrictions.

---

## Transfer Behavior

Uploads/downloads:

* Have no strict timeout
* Continue until:

  * completion
  * user cancellation
  * unrecoverable error

---

# Architecture

## Frontend

* Avalonia UI
* C# / .NET

## Backend

* Native C library
* libcurl
* SQLite
* AES-256-GCM crypto implementation

---

# Roadmap

Planned improvements before beta:

* Drag & drop uploads
* Multi-file selection improvements
* Better preview system
* UI overhaul/polish
* Search system
* Parallel chunk downloads
* Transfer queue system
* Resume support
* File integrity verification
* Hash validation
* Better diagnostics/logging
* Better folder UX
* Performance optimizations

---

# Known Limitations

DiscBox depends heavily on Discord infrastructure.

Possible limitations include:

* Discord rate limits
* Webhook restrictions
* Attachment availability
* CDN propagation delays
* Discord API behavior changes

---

# Disclaimer

DiscBox is an experimental educational project.

It is not production-ready and should not be used for important or irreplaceable data.

The project relies on Discord infrastructure in ways it was not originally designed for, so long-term compatibility and reliability are not guaranteed.

---

# License

Open-source project — license to be defined.
