# SharpSFV

**SharpSFV** is a modern, high-performance file hashing and verification tool built in C# (.NET 10). Designed as a spiritual successor to the classic QuickSFV, it retains a familiar, lightweight interface while introducing modern algorithms, smart hardware detection, and the robust file handling required for massive datasets.

## Improvements over QuickSFV

SharpSFV addresses the limitations of legacy tools to meet modern computing standards:

*   **Smart Threading:** Automatically detects if files are on a mechanical **HDD** or an **SSD/NVMe**. It switches between sequential processing (to avoid disk thrashing) and massive parallelism (to maximize CPU usage) automatically.
*   **Virtual Mode Scalability:** Capable of handling lists with **500.000+ files** with zero UI lag or memory overhead.
*   **Active Large File Pinning:** Files larger than 1GB are temporarily "pinned" to the top of the list during processing so they don't get lost in the scrollbar, returning to their sorted position upon completion.
*   **Legacy Compatibility:** Robust parsing engine that supports both modern `Hash *Filename` and legacy `Filename Hash` syntax. It also automatically detects and handles **Endianness differences** in older CRC-32 checksums.
*   **Modern Algorithms:** Implements **xxHash-3 (128-bit)**, which is orders of magnitude faster than standard algorithims, i.e. MD5, SHA1, CRC-32.
*   **Robust Path Handling:** Full support for Long Paths (260+ characters) and Unicode characters, preventing errors present in QuickSFV.

## Key Features

*   **Drag-and-Drop Workflow:** Seamlessly handles files, folders (recursive scanning), and existing checksum files via drag-and-drop.
*   **Search & Filter:** Instant filtering by filename or status (e.g., quickly isolate "BAD" or "MISSING" files).
*   **Context Menu Actions:** Right-click files to Open Containing Folder, Copy Path/Hash, Rename, or Delete files directly from the UI.
*   **Batch Cleanup:** Automatically generate a `.bat` script to delete all files marked as "BAD" during verification.
*   **Portable Configuration:** All settings are saved to a local `SharpSFV.ini` file, ensuring no registry bloat, with clear, human-readable values, unlike QuickSFV.
*   **Clipboard Integration:** Copy details to clipboard or Paste (`Ctrl+V`) a list of paths directly into the queue.

## Supported Algorithms

*   **xxHash-3 (128-bit):** Default. Extremely high performance; ideal for verifying large datasets or game files.
*   **CRC-32:** Full support for standard `.sfv` files, including legacy Endian swapping.
*   **MD5:** Standard industry hash for file integrity.
*   **SHA-1:** Older standard, widely used for legacy verification.
*   **SHA-256:** Secure cryptographic hash for high-integrity requirements.

## Usage

### Creating Checksums
1.  Drag and drop files or folders into the SharpSFV window.
2.  (Optional) Paste file paths using `Ctrl+V`.
3.  Select your desired algorithm from the **Options** menu (defaults to xxHash-3).
4.  Wait for processing to complete.
5.  Click **File > Save As...** to generate the checksum file.

### Verifying Files
1.  Drag and drop a checksum file (`.sfv`, `.md5`, `.xxh3`, etc.) into the window.
2.  SharpSFV will automatically detect the format and verify entries relative to the checksum file's location.
3.  Files will be marked as **OK** (Green), **BAD** (Red), or **MISSING** (Strikethrough).

### Command Line
You can open SharpSFV and immediately verify a file by passing it as an argument:
```cmd
SharpSFV.exe <checksum_file.sfv>

## Usage
```
SharpSFV <checksum.sfv/md5/sha1/sha256/xxh3>
```

## Configuration

Settings are stored in `SharpSFV.ini`. You can modify them via the **Options** menu:

*   **Enable Time Elapsed Tab:** Displays the time taken to process each individual file.
*   **Always Save Absolute Paths:** Forces the output file to use full drive paths instead of relative paths.
*   **Show Search/Filter Bar:** Toggles the visibility of the filtering toolbar.

## Building
```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```
## Build Requirements

*   **IDE:** Visual Studio 2022 (or newer) or JetBrains Rider.
*   **Framework:** .NET 8.0 (or newer).
*   **Dependencies:** `System.IO.Hashing` (NuGet package).

## License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**.

You may copy, distribute, and modify the software as long as you track changes/dates in source files. Any modifications to or software including (via compiler) GPL-licensed code must also be made available under the GPL along with build & install instructions.

See the [LICENSE](LICENSE.txt) file for more details.