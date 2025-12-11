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

## Usage

1. Click "Browse..." to select a .3mf file
2. Check the operations you want to perform
3. Click "Process File"
4. The modified file will be saved with "_modified" suffix
5. A backup of the original file will be created with ".backup" extension

## Project Structure

- `MainWindow.xaml` - UI layout
- `MainWindow.xaml.cs` - Main logic and file processing
- `App.xaml` / `App.xaml.cs` - Application entry point
- `BambuStripper.csproj` - Project file

## Notes

- .3MF files are ZIP archives, so this tool works by extracting and re-archiving with modifications
- Always keep backups of important files
- The tool creates automatic backups, but you should still maintain your own backups

