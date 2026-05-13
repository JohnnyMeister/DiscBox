# DiscBox

DiscBox is an experimental open-source personal cloud drive that uses Discord webhooks as a free remote storage backend.

The project focuses on creating a modern desktop cloud storage experience with:

* File uploads/downloads
* Folder management
* Chunked file storage
* Virtual filesystem support
* Local database indexing
* Discord-backed storage
* Modern Avalonia UI
* Transfer progress tracking
* Encrypted storage support

> ⚠️ DiscBox is currently in active development and should be considered experimental.

---

# Features

## File Explorer

* Modern desktop file explorer UI
* Folder navigation
* Breadcrumb navigation
* Context menu support
* File/folder icons
* Sorting-ready architecture
* Status messages and operation feedback

## File Operations

* Upload files
* Download files
* Delete files/folders
* Rename entries
* Create folders
* Copy virtual paths
* File properties dialog
* Cut / Copy / Paste support
* Move files between folders

## Transfer System

* Chunked uploads using Discord webhooks
* Upload progress window
* Live speed display
* ETA estimation
* Large file support
* Background async operations
* Transfer cancellation support

## Storage System

* Virtual filesystem
* SQLite metadata database
* Chunk-based storage architecture
* Discord attachment storage
* File reconstruction from chunks

---

# Current Project Status

DiscBox is functional for many basic operations, but the project is still heavily under development.

## Currently Working

✅ Uploading files

✅ Creating folders

✅ File explorer navigation

✅ Rename operations

✅ Context menu actions

✅ Chunked storage system

✅ Progress tracking UI

✅ Large file uploads (improved)

✅ Virtual filesystem structure

✅ Local metadata/database handling

## Known Issues

### Imported DiscBox Files Cannot Be Downloaded

There is currently a major unresolved bug affecting files imported through DiscBox.

Symptoms:

* Some imported videos fail to download correctly
* Downloaded files may appear corrupted
* Windows may refuse to open downloaded media files
* The issue mainly affects files reconstructed from Discord chunks

Important:

* Files stored directly on Discord usually remain valid
* The corruption appears during chunk retrieval or reconstruction
* The exact root cause is still unknown

Current investigation areas:

* Discord message fetch reliability
* Chunk ordering validation
* Missing chunk edge cases
* CURL handle state issues
* Discord CDN attachment behavior
* Chunk reconstruction alignment

### Download System Is Still Work In Progress

The DiscBox download system is still being actively rewritten and stabilized.

Known limitations:

* Some downloads may fail unexpectedly
* Imported files are unreliable
* Chunk reconstruction is not fully stable
* Error recovery is incomplete
* Integrity verification is not yet implemented

### Discord API Limitations

DiscBox depends heavily on Discord webhook behavior.

Possible external limitations include:

* Rate limiting
* Attachment availability
* Message endpoint inconsistencies
* Webhook restrictions
* CDN propagation delays

---

# Technical Details

## Chunk System

Files are split into chunks before upload.

Current chunk size:

* 10MB per chunk

This was changed from 25MB after discovering Discord webhook upload limits caused failures for larger chunks.

## Timeouts

Transfer timeout limits were removed.

Uploads and downloads now continue indefinitely unless:

* An error occurs
* The user cancels the operation

## UI Stack

Frontend:

* Avalonia UI
* C# / .NET

Backend:

* Native C library
* libcurl
* SQLite

---

# Roadmap

Planned improvements include:

* Stable download reconstruction
* File integrity verification
* Hash validation
* Retry logic for failed chunks
* Parallel chunk downloads
* Drag & drop support
* Search system
* Better folder operations
* Download queue management
* Multi-threaded transfers
* Better Discord API abstraction
* Improved error diagnostics
* Transfer resume support

---

# Disclaimer

DiscBox is an experimental educational project.

It is not production-ready and should not be used for important or irreplaceable files.

Because the project relies on Discord infrastructure in unintended ways, stability and long-term compatibility are not guaranteed.

---

# License

Open-source project — license to be defined.
