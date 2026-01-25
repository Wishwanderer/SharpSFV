# SharpSFV

**SharpSFV** is a modern, high-performance file hashing and verification tool built in **C# (.NET 10)**. Designed as a spiritual successor to the classic QuickSFV, it retains a familiar, lightweight interface while leveraging **Data-Oriented Design (DoD)**, smart hardware detection, and Zero-Allocation memory management to handle massive datasets.

---

## QuickSFV Comparison Benchmark (MD5)

*Benchmark performed on a dataset of **500,000 files** (ranging from 1KB to 10MB).*

| Tool | Result | Notes |
| :--- | :--- | :--- |
| **QuickSFV** | **DNF (Crashed)** | Crashed at ~250,000 files. Process took ~10m 20s before failure. |
| **SharpSFV** | **74,382ms (75s)** | Completed successfully with full UI responsiveness. |

---

## Technical Improvements

SharpSFV addresses the architectural limitations of legacy tools to meet modern computing standards:

*   **Structure of Arrays (SoA):** Unlike standard object-oriented apps, SharpSFV uses parallel arrays to manage data. This reduces memory overhead by ~60% and maximizes CPU cache locality.
*   **Smart I/O Threading:** Automatically probes the hardware to detect **HDD** vs **SSD/NVMe**.
    *   *HDD:* Switches to **Sequential** processing to prevent disk thrashing.
    *   *SSD:* Switches to **Massive Parallelism** to saturate the PCIe bus.
*   **Zero-Allocation Pipeline:** Utilizes `ArrayPool`, `Span<T>`, and `System.Threading.Channels` to eliminate Garbage Collection (GC) pauses during processing.
*   **Active Jobs View:** Files larger than **50MB** trigger a secondary "Active Jobs" panel, allowing you to monitor individual progress without the UI freezing on a single file.
*   **Legacy Compatibility:** Robust parsing engine that handles both `Hash *Filename` and `Filename Hash` syntax. Automatically detects and fixes **Endianness mismatches** in legacy CRC-32 checksums.
*   **Modern Algorithms:** Implements **xxHash-3 (128-bit)**, providing checksum generation at the speed of RAM (GB/s).

---

## Key Features

*   **Multiple Operation Modes:**
    *   **Standard Mode:** Classic list view for verifying or creating checksums.
    *   **Job Queue Mode:** Dragging folders queues them as distinct jobs, processed sequentially.
    *   **Mini Mode:** A compact UI for quick Context Menu operations.
    *   **Headless Mode:** CLI execution for scripting and automation.
*   **Context Menu Integration:** Right-click files/folders in Explorer to "Create Checksum". Handles multi-instance piping automatically.
*   **Bad File Tools:**
    *   Move "BAD" files to a `_BAD_FILES` subfolder.
    *   Rename corrupt files (append `.CORRUPT`).
    *   Generate `.bat` scripts to delete bad files or handle duplicates.
*   **Instant Filtering:** Real-time search by filename or status (e.g., filter to show only "MISSING" files).
*   **Portable Configuration:** Settings are saved to `SharpSFV.ini`. No Registry bloat.

---

## Supported Algorithms

| Algorithm | Bit Width | Description |
| :--- | :--- | :--- |
| **xxHash-3** | 128-bit | **Default.** Extremely fast. Ideal for game files and large datasets. |
| **CRC-32** | 32-bit | Standard `.sfv` support. Includes legacy Endian-swap logic. |
| **MD5** | 128-bit | Industry standard for file integrity verification. |
| **SHA-1** | 160-bit | Legacy standard support. |
| **SHA-256** | 256-bit | High-security cryptographic hash. |

---

## Usage

### GUI Modes
1.  **Creation:** Drag files/folders into the window. Select Algo > `File` > `Save As...`.
2.  **Verification:** Drag a checksum file (`.sfv`, `.md5`, etc.) into the window.
    *   *Green:* OK
    *   *Red:* BAD (Hash Mismatch)
    *   *Strikethrough:* MISSING (File not found)
3.  **Job Queue:** Select "Mode > Job Queue Mode", then drag multiple folders to queue them as batch jobs.

### Command Line (Headless)
SharpSFV can be used in scripts without showing a window using the `-headless` flag. It attaches to the parent console to stream output.

```cmd
:: Verify a file silently
SharpSFV.exe -headless "C:\LinuxDistros\ubuntu.md5"

:: Create checksums for a folder (Default Algo)
SharpSFV.exe -headless -create "C:\Backups\2023"
```

### Context Menu Registration
To enable right-click integration:
1.  Run SharpSFV as Administrator (optional, but recommended for registry writes).
2.  Go to **Options > System Integration > Register Explorer Context Menu**.

---

## Configuration (`SharpSFV.ini`)

Settings can be toggled via the **Options** menu or edited manually in the `.ini` file:

*   `ProcessingMode`: Forces `HDD` (Sequential) or `SSD` (Parallel). Default is `Auto`.
*   `PathStorageMode`: `Relative` (default) or `Absolute`.
*   `EnableChecksumComments`: Adds header comments (Date/Algo) to generated files.
*   `ShowThroughputStats`: Toggles the Speed (MB/s) and ETA display.
*   `OptimizeForHDD`: *Legacy flag, superseded by ProcessingMode.*

---

## Build Instructions

**Requirements:**
*   .NET 10.0 SDK
*   Visual Studio 2022 / JetBrains Rider

**Build Command:**
```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

---

## License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**.

You may copy, distribute, and modify the software as long as you track changes/dates in source files. Any modifications to or software including (via compiler) GPL-licensed code must also be made available under the GPL along with build & install instructions.