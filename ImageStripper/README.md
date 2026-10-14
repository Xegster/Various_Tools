# ImageStripper

A small WPF tool for viewing image metadata and removing selected tags in bulk.

## What it does

1. Point it at a file, several files, or a folder (browse buttons or drag and drop).
2. It reads every metadata entry it can find in each image and lists them with a
   friendly name, the value, how many of the checked images carry the tag, and the
   raw WIC query path.
3. Tick the tags you want gone, then press **Remove Selected Tags**. The selection is
   applied to every checked image.

Tag checkboxes are keyed on the metadata query path, so a tag stays selected while you
click between files. Tick **Show tags from all checked images** to work from the union
of every image's tags instead of just the highlighted one.

## How removal works

Metadata is read and written through the Windows Imaging Component (no external
dependencies).

- **In place (preferred).** The metadata blocks inside the existing file are rewritten
  and the compressed pixel data is left alone. This is lossless and is what JPEG and
  TIFF normally use.
- **Re-encode (fallback).** If the codec refuses an in-place write, the image is decoded
  and written back out without the selected tags. That is lossless for PNG/TIFF/BMP/GIF
  but recompresses JPEG (quality 95). The log says which path was taken per file.

Selecting an entire block (for example the `gps` or `xmp` row marked *entire block*)
removes everything underneath it in one go.

### Options

- **Back up originals to a Backup subfolder** - copies each original into a `Backup`
  folder next to it before writing. An existing backup is never overwritten, so the
  first copy stays pristine. Files inside `Backup` folders are skipped when scanning.
- **Preserve file dates** - restores the original created/modified timestamps after a
  file is rewritten.

## Supported formats

JPEG, PNG, TIFF, GIF, BMP, HD Photo/JPEG-XR, ICO and DNG - whatever WIC can decode on
the machine. How much metadata can be removed depends on the codec.

## Build and run

Requires the .NET 8 SDK (or newer with the net8.0 targeting pack).

```
dotnet build ImageStripper.csproj
dotnet run --project ImageStripper.csproj
```

## Publish

```
dotnet publish ImageStripper.csproj -c Release
```

Produces a single-file, framework-dependent executable at:

```
bin/Release/net8.0-windows/win-x64/publish/ImageStripper.exe
```

It needs the .NET 8 Desktop Runtime installed. To make it fully standalone, set
`<SelfContained>true</SelfContained>` in `ImageStripper.csproj`.
