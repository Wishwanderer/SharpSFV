# SharpSFV

**SharpSFV** is a modern, high-performance file hashing and verification tool built in C#. Designed as a spiritual successor to the classic QuickSFV, it retains the familiar, lightweight interface while introducing modern algorithms, robust file handling, and quality-of-life features required for today's file systems.

## Improvements over QuickSFV

SharpSFV addresses several limitations of the original QuickSFV engine to meet modern computing standards:

*   **Modern Algorithms:** Implements xxHash-3 (128-bit), which is significantly faster and more secure than the standard CRC-32 used by QuickSFV.
*   **Recursive Scanning:** Native support for dragging and dropping folders to automatically find and hash all files within them and their subdirectories.
*   **Robust Path Handling:** Includes safeguards and warnings for file paths exceeding standard Windows limits (260+ characters), preventing the crashes common in older tools.
*   **Unicode Support:** Fully supports international characters in filenames and paths, resolving issues where QuickSFV fails to read non-Latin characters.
*   **Enhanced UI:** Features dual progress bars (Current File vs. Total Batch) and a toggleable search/filter bar for managing large lists.
*   **Clipboard Integration:** Supports pasting lists of file paths directly from the clipboard and copying verification results for reporting.

## Key Features

*   **Drag-and-Drop Workflow:** Seamlessly handles files, folders, and existing checksum files via drag-and-drop.
*   **Search & Filter:** Filter the file list by filename or status (e.g., quickly isolate "BAD" or "MISSING" files).
*   **Context Menu Actions:** Right-click files to Open Containing Folder, Copy Path/Hash, Rename, or Delete files directly from the UI.
*   **Batch Cleanup:** Automatically generate a `.bat` script to delete all files marked as "BAD" during verification.
*   **Portable Configuration:** All settings are saved to a local `SharpSFV.ini` file next to the executable, ensuring no registry bloat.
*   **Path Flexibility:** Option to toggle between saving Relative paths (default) or Absolute paths in checksum files.

## Supported Algorithms

*   **xxHash-3 (128-bit):** Default. Extremely high performance; ideal for verifying large datasets or game files.
*   **CRC-32:** Retained for backward compatibility with `.sfv` files.
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
2.  SharpSFV will automatically parse the file and verify all entries relative to the checksum file's location.
3.  Files will be marked as **OK** (Green), **BAD** (Red), or **MISSING** (Strikethrough).

## Configuration

Settings are stored in `SharpSFV.ini`. You can modify them via the **Options** menu:

*   **Enable Time Elapsed Tab:** Displays the time taken to process each individual file.
*   **Always Save Absolute Paths:** Forces the output file to use full drive paths instead of relative paths.
*   **Show Search/Filter Bar:** Toggles the visibility of the filtering toolbar.

## Build Requirements

*   **IDE:** Visual Studio 2022 (or newer) or JetBrains Rider.
*   **Framework:** .NET 6.0 (or newer).
*   **Dependencies:** `System.IO.Hashing` (NuGet package).

## License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**.

You may copy, distribute, and modify the software as long as you track changes/dates in source files. Any modifications to or software including (via compiler) GPL-licensed code must also be made available under the GPL along with build & install instructions.

See the [LICENSE](LICENSE) file for more details.
