# BambuStripper

A simple WPF application for modifying .3MF files. This tool allows you to remove metadata, thumbnails, print tickets, and other non-essential content from 3MF files.

## Features

- Remove metadata files (model.rels, [Content_Types].xml modifications)
- Remove thumbnail images
- Remove print ticket files
- Clean model files (remove non-essential entries)
- Automatic backup creation
- Simple, intuitive UI

## Requirements

- .NET 8.0 SDK or later
- Windows OS

## Building

1. Open a terminal in the project directory
2. Run:
   ```
   dotnet build
   ```
3. Run:
   ```
   dotnet run
   ```

Or open the project in Visual Studio and press F5.

## Publishing

You can publish the application as a standalone executable. There are two options:

### Self-Contained (With .NET Runtime Bundled)

This creates a single executable file that includes the .NET 8 runtime. The executable is larger (~170 MB) but can run on any Windows x64 machine without requiring .NET to be installed.

1. Open a terminal in the project directory
2. Run:
   ```
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
   ```
3. The executable will be created at:
   ```
   bin\Release\net8.0-windows\win-x64\publish\BambuStripper.exe
   ```

**Note:** The project file is already configured with these settings, so you can also simply run:

```
dotnet publish -c Release -r win-x64
```

### Framework-Dependent (Without .NET Runtime)

This creates a smaller executable (~1-2 MB) that requires .NET 8.0 Runtime to be installed on the target machine.

1. Open a terminal in the project directory
2. Run:
   ```
   dotnet publish -c Release -p:PublishSingleFile=true --self-contained false
   ```
3. The executable will be created at:
   ```
   bin\Release\net8.0-windows\publish\BambuStripper.exe
   ```

**Note:** Users will need to have the [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed to run this version.

## Usage

1. Click "Browse..." to select a .3mf file
2. Check the operations you want to perform
3. Click "Process File"
4. The original file will be overwritten with the modified version
5. A backup of the original file will be created in the "Backup" folder

## Project Structure

- `MainWindow.xaml` - UI layout
- `MainWindow.xaml.cs` - Main logic and file processing
- `App.xaml` / `App.xaml.cs` - Application entry point
- `BambuStripper.csproj` - Project file

## Notes

- .3MF files are ZIP archives, so this tool works by extracting and re-archiving with modifications
- Always keep backups of important files
- The tool creates automatic backups, but you should still maintain your own backups
