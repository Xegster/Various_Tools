using System;
using System.Collections.Generic;
using System.Globalization;

namespace ImageStripper
{
    /// <summary>
    /// Translates WIC metadata query paths (e.g. "/app1/ifd/gps/{ushort=2}") into a
    /// human readable group + tag name so the UI can show something friendlier than
    /// the raw query string.
    /// </summary>
    public static class TagCatalog
    {
        public const string GroupGps = "GPS";
        public const string GroupExif = "Exif";
        public const string GroupInterop = "Exif Interop";
        public const string GroupThumbnail = "Exif Thumbnail";
        public const string GroupIfd = "TIFF / IFD0";
        public const string GroupXmp = "XMP";
        public const string GroupIptc = "IPTC";

        /// <summary>Returns the display group and tag name for a full metadata query.</summary>
        public static (string Group, string Name) Describe(string query, bool isContainer)
        {
            string lower = query.ToLowerInvariant();
            string group = ResolveGroup(lower);
            string segment = LastSegment(query);
            string name = isContainer
                ? FriendlyContainer(segment) + " - entire block"
                : ResolveName(segment, group);

            return (group, name);
        }

        private static string ResolveGroup(string lower)
        {
            if (lower.StartsWith("/xmp")) return GroupXmp;
            if (lower.Contains("iptc")) return GroupIptc;
            if (lower.StartsWith("/app13")) return "Photoshop IRB";
            if (lower.StartsWith("/app0")) return "JFIF";
            if (lower.StartsWith("/app2")) return "ICC Profile";
            if (lower.StartsWith("/app14")) return "Adobe APP14";
            if (lower.StartsWith("/com")) return "JPEG Comment";
            if (lower.StartsWith("/app1/thumb")) return GroupThumbnail;
            if (lower.Contains("/interop")) return GroupInterop;
            if (lower.Contains("/gps")) return GroupGps;
            if (lower.Contains("/exif")) return GroupExif;
            if (lower.StartsWith("/app1") || lower.StartsWith("/ifd")) return GroupIfd;
            if (lower.StartsWith("/text") || lower.StartsWith("/itxt") || lower.StartsWith("/ztxt")) return "PNG Text";
            if (lower.StartsWith("/time") || lower.StartsWith("/gama") || lower.StartsWith("/chrm") ||
                lower.StartsWith("/srgb") || lower.StartsWith("/phys") || lower.StartsWith("/bkgd") ||
                lower.StartsWith("/sbit") || lower.StartsWith("/hist")) return "PNG Chunk";
            if (lower.StartsWith("/grctlext") || lower.StartsWith("/imgdesc") || lower.StartsWith("/logscrdesc") ||
                lower.StartsWith("/appext") || lower.StartsWith("/commentext")) return "GIF";
            return "Other";
        }

        private static string ResolveName(string segment, string group)
        {
            string? inner = Unwrap(segment, "ushort");
            if (inner != null && ushort.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort id))
            {
                string? known = LookupTag(group, id);
                return known ?? $"Tag {id} (0x{id:X4})";
            }

            inner = Unwrap(segment, "uint");
            if (inner != null && uint.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uid))
            {
                if (group == GroupIptc && IptcTags.TryGetValue(uid, out string? iptc)) return iptc;
                return $"Tag {uid}";
            }

            inner = Unwrap(segment, "str") ?? Unwrap(segment, "wstr") ?? Unwrap(segment, "long") ?? Unwrap(segment, "ulong");
            return inner ?? segment;
        }

        /// <summary>Extracts "271" out of "{ushort=271}"; returns null when the shape does not match.</summary>
        private static string? Unwrap(string segment, string type)
        {
            string prefix = "{" + type + "=";
            if (segment.Length <= prefix.Length) return null;
            if (!segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            if (!segment.EndsWith("}", StringComparison.Ordinal)) return null;
            return segment.Substring(prefix.Length, segment.Length - prefix.Length - 1);
        }

        /// <summary>
        /// Returns the part of the query after the final separating slash. Slashes nested
        /// inside braces or inside angle brackets (XMP namespace URIs) are not separators.
        /// </summary>
        public static string LastSegment(string query)
        {
            int braces = 0;
            int angles = 0;
            int lastSlash = -1;

            for (int i = 0; i < query.Length; i++)
            {
                char c = query[i];
                if (c == '{') braces++;
                else if (c == '}') { if (braces > 0) braces--; }
                else if (c == '<') angles++;
                else if (c == '>') { if (angles > 0) angles--; }
                else if (c == '/' && braces == 0 && angles == 0) lastSlash = i;
            }

            return lastSlash >= 0 && lastSlash < query.Length - 1
                ? query.Substring(lastSlash + 1)
                : query;
        }

        private static string FriendlyContainer(string segment)
        {
            return segment.ToLowerInvariant() switch
            {
                "app0" => "JFIF",
                "app1" => "APP1 (Exif / XMP)",
                "app2" => "ICC profile",
                "app13" => "Photoshop APP13",
                "app14" => "Adobe APP14",
                "ifd" => "IFD0",
                "exif" => "Exif sub-IFD",
                "gps" => "GPS",
                "interop" => "Interop IFD",
                "thumb" => "Exif thumbnail",
                "xmp" => "XMP packet",
                "irb" => "Photoshop IRB",
                "8bimiptc" => "8BIM IPTC",
                "8bimresinfo" => "8BIM resolution info",
                "iptc" => "IPTC",
                _ => segment
            };
        }

        private static string? LookupTag(string group, ushort id)
        {
            switch (group)
            {
                case GroupGps:
                    return GpsTags.TryGetValue(id, out string? gps) ? gps : null;
                case GroupInterop:
                    return InteropTags.TryGetValue(id, out string? interop) ? interop : null;
                case GroupExif:
                    if (ExifTags.TryGetValue(id, out string? exif)) return exif;
                    return TiffTags.TryGetValue(id, out string? exifFallback) ? exifFallback : null;
                default:
                    if (TiffTags.TryGetValue(id, out string? tiff)) return tiff;
                    return ExifTags.TryGetValue(id, out string? tiffFallback) ? tiffFallback : null;
            }
        }

        // ---- Tag number tables -------------------------------------------------

        private static readonly Dictionary<ushort, string> TiffTags = new()
        {
            { 254, "NewSubfileType" },
            { 255, "SubfileType" },
            { 256, "ImageWidth" },
            { 257, "ImageHeight" },
            { 258, "BitsPerSample" },
            { 259, "Compression" },
            { 262, "PhotometricInterpretation" },
            { 266, "FillOrder" },
            { 269, "DocumentName" },
            { 270, "ImageDescription" },
            { 271, "Camera Make" },
            { 272, "Camera Model" },
            { 273, "StripOffsets" },
            { 274, "Orientation" },
            { 277, "SamplesPerPixel" },
            { 278, "RowsPerStrip" },
            { 279, "StripByteCounts" },
            { 282, "XResolution" },
            { 283, "YResolution" },
            { 284, "PlanarConfiguration" },
            { 296, "ResolutionUnit" },
            { 297, "PageNumber" },
            { 301, "TransferFunction" },
            { 305, "Software" },
            { 306, "DateTime (modified)" },
            { 315, "Artist" },
            { 316, "HostComputer" },
            { 317, "Predictor" },
            { 318, "WhitePoint" },
            { 319, "PrimaryChromaticities" },
            { 320, "ColorMap" },
            { 330, "SubIFDs" },
            { 338, "ExtraSamples" },
            { 339, "SampleFormat" },
            { 347, "JPEGTables" },
            { 513, "ThumbnailOffset" },
            { 514, "ThumbnailLength" },
            { 529, "YCbCrCoefficients" },
            { 530, "YCbCrSubSampling" },
            { 531, "YCbCrPositioning" },
            { 532, "ReferenceBlackWhite" },
            { 700, "XMP packet" },
            { 33421, "CFARepeatPatternDim" },
            { 33422, "CFAPattern" },
            { 33432, "Copyright" },
            { 34377, "Photoshop ImageResources" },
            { 34665, "Exif IFD pointer" },
            { 34675, "ICC Profile" },
            { 34853, "GPS IFD pointer" },
            { 37724, "Photoshop ImageSourceData" },
            { 40091, "XP Title" },
            { 40092, "XP Comment" },
            { 40093, "XP Author" },
            { 40094, "XP Keywords" },
            { 40095, "XP Subject" },
            { 50341, "PrintImageMatching" },
            { 50706, "DNGVersion" },
            { 50708, "UniqueCameraModel" }
        };

        private static readonly Dictionary<ushort, string> ExifTags = new()
        {
            { 33434, "ExposureTime" },
            { 33437, "FNumber" },
            { 34850, "ExposureProgram" },
            { 34852, "SpectralSensitivity" },
            { 34855, "ISO Speed Ratings" },
            { 34856, "OECF" },
            { 34864, "SensitivityType" },
            { 34865, "StandardOutputSensitivity" },
            { 34866, "RecommendedExposureIndex" },
            { 34867, "ISOSpeed" },
            { 34868, "ISOSpeedLatitudeyyy" },
            { 34869, "ISOSpeedLatitudezzz" },
            { 36864, "ExifVersion" },
            { 36867, "DateTimeOriginal" },
            { 36868, "DateTimeDigitized" },
            { 36880, "OffsetTime" },
            { 36881, "OffsetTimeOriginal" },
            { 36882, "OffsetTimeDigitized" },
            { 37121, "ComponentsConfiguration" },
            { 37122, "CompressedBitsPerPixel" },
            { 37377, "ShutterSpeedValue" },
            { 37378, "ApertureValue" },
            { 37379, "BrightnessValue" },
            { 37380, "ExposureBiasValue" },
            { 37381, "MaxApertureValue" },
            { 37382, "SubjectDistance" },
            { 37383, "MeteringMode" },
            { 37384, "LightSource" },
            { 37385, "Flash" },
            { 37386, "FocalLength" },
            { 37396, "SubjectArea" },
            { 37500, "MakerNote" },
            { 37510, "UserComment" },
            { 37520, "SubsecTime" },
            { 37521, "SubsecTimeOriginal" },
            { 37522, "SubsecTimeDigitized" },
            { 40960, "FlashpixVersion" },
            { 40961, "ColorSpace" },
            { 40962, "PixelXDimension" },
            { 40963, "PixelYDimension" },
            { 40964, "RelatedSoundFile" },
            { 40965, "Interop IFD pointer" },
            { 41483, "FlashEnergy" },
            { 41484, "SpatialFrequencyResponse" },
            { 41486, "FocalPlaneXResolution" },
            { 41487, "FocalPlaneYResolution" },
            { 41488, "FocalPlaneResolutionUnit" },
            { 41492, "SubjectLocation" },
            { 41493, "ExposureIndex" },
            { 41495, "SensingMethod" },
            { 41728, "FileSource" },
            { 41729, "SceneType" },
            { 41730, "CFAPattern" },
            { 41985, "CustomRendered" },
            { 41986, "ExposureMode" },
            { 41987, "WhiteBalance" },
            { 41988, "DigitalZoomRatio" },
            { 41989, "FocalLengthIn35mmFilm" },
            { 41990, "SceneCaptureType" },
            { 41991, "GainControl" },
            { 41992, "Contrast" },
            { 41993, "Saturation" },
            { 41994, "Sharpness" },
            { 41995, "DeviceSettingDescription" },
            { 41996, "SubjectDistanceRange" },
            { 42016, "ImageUniqueID" },
            { 42032, "Camera Owner Name" },
            { 42033, "Body Serial Number" },
            { 42034, "LensSpecification" },
            { 42035, "Lens Make" },
            { 42036, "Lens Model" },
            { 42037, "Lens Serial Number" },
            { 42080, "CompositeImage" },
            { 42240, "Gamma" }
        };

        private static readonly Dictionary<ushort, string> GpsTags = new()
        {
            { 0, "GPS VersionID" },
            { 1, "GPS LatitudeRef" },
            { 2, "GPS Latitude" },
            { 3, "GPS LongitudeRef" },
            { 4, "GPS Longitude" },
            { 5, "GPS AltitudeRef" },
            { 6, "GPS Altitude" },
            { 7, "GPS TimeStamp" },
            { 8, "GPS Satellites" },
            { 9, "GPS Status" },
            { 10, "GPS MeasureMode" },
            { 11, "GPS DOP" },
            { 12, "GPS SpeedRef" },
            { 13, "GPS Speed" },
            { 14, "GPS TrackRef" },
            { 15, "GPS Track" },
            { 16, "GPS ImgDirectionRef" },
            { 17, "GPS ImgDirection" },
            { 18, "GPS MapDatum" },
            { 19, "GPS DestLatitudeRef" },
            { 20, "GPS DestLatitude" },
            { 21, "GPS DestLongitudeRef" },
            { 22, "GPS DestLongitude" },
            { 23, "GPS DestBearingRef" },
            { 24, "GPS DestBearing" },
            { 25, "GPS DestDistanceRef" },
            { 26, "GPS DestDistance" },
            { 27, "GPS ProcessingMethod" },
            { 28, "GPS AreaInformation" },
            { 29, "GPS DateStamp" },
            { 30, "GPS Differential" },
            { 31, "GPS HPositioningError" }
        };

        private static readonly Dictionary<ushort, string> InteropTags = new()
        {
            { 1, "InteroperabilityIndex" },
            { 2, "InteroperabilityVersion" },
            { 4096, "RelatedImageFileFormat" },
            { 4097, "RelatedImageWidth" },
            { 4098, "RelatedImageHeight" }
        };

        private static readonly Dictionary<uint, string> IptcTags = new()
        {
            { 5, "Object Name / Title" },
            { 10, "Urgency" },
            { 15, "Category" },
            { 20, "Supplemental Categories" },
            { 25, "Keywords" },
            { 40, "Special Instructions" },
            { 55, "Date Created" },
            { 60, "Time Created" },
            { 65, "Originating Program" },
            { 80, "By-line (Author)" },
            { 85, "By-line Title" },
            { 90, "City" },
            { 92, "Sub-location" },
            { 95, "Province / State" },
            { 100, "Country Code" },
            { 101, "Country Name" },
            { 103, "Original Transmission Reference" },
            { 105, "Headline" },
            { 110, "Credit" },
            { 115, "Source" },
            { 116, "Copyright Notice" },
            { 118, "Contact" },
            { 120, "Caption / Abstract" },
            { 122, "Writer / Editor" }
        };
    }
}
