﻿// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using MonoGame.Framework.Utilities;
using SharpFont;
using Glyph = Microsoft.Xna.Framework.Content.Pipeline.Graphics.Glyph;

namespace Microsoft.Xna.Framework.Content.Pipeline.Processors
{
    [ContentProcessor(DisplayName = "Sprite Font Description - MonoGame")]
    public class FontDescriptionProcessor : ContentProcessor<FontDescription, SpriteFontContent>
    {
        [DefaultValue(true)]
        public virtual bool PremultiplyAlpha { get; set; }

        [DefaultValue(typeof(TextureProcessorOutputFormat), "Compressed")]
        public virtual TextureProcessorOutputFormat TextureFormat { get; set; }

        public FontDescriptionProcessor()
        {
            PremultiplyAlpha = true;
            TextureFormat = TextureProcessorOutputFormat.Color;
        }

        public override SpriteFontContent Process(FontDescription input,
            ContentProcessorContext context)
        {
            var output = new SpriteFontContent(input);
            var fontFile = FindFont(input.FontName, input.Style, context.FontDir);

            context.Logger.LogMessage("Using font file: {0}", fontFile);

            if (string.IsNullOrWhiteSpace(fontFile))
            {
                var directories = new List<string> { Path.GetDirectoryName(input.Identity.SourceFilename) };
                var extensions = new string[] { "", ".ttf", ".ttc", ".otf" };

                // Add special per platform directories
                if (CurrentPlatform.OS == OS.Windows)
                    directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"));
                else if (CurrentPlatform.OS == OS.MacOSX)
                {
                    directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Fonts"));
                    directories.Add("/Library/Fonts");
                    directories.Add("/System/Library/Fonts/Supplemental");
                }

                foreach (var dir in directories)
                {
                    foreach (var ext in extensions)
                    {
                        fontFile = Path.Combine(dir, input.FontName + ext);
                        if (File.Exists(fontFile))
                            break;
                    }
                    if (File.Exists(fontFile))
                        break;
                }
            }

            if (!File.Exists(fontFile))
            {
                throw new FileNotFoundException("Could not find \"" + input.FontName + "\" font file.");
            }
            context.Logger.LogMessage("Building Font {0}", fontFile);

            // Get the platform specific texture profile.
            var texProfile = TextureProfile.ForPlatform(context.TargetPlatform);

            {
                //if (!File.Exists(fontFile))
                //{
                //    throw new Exception(string.Format("Could not load {0}", fontFile));
                //}
                var lineSpacing = 0f;
                int yOffsetMin = 0;
                var glyphs = ImportFont(input, out lineSpacing, out yOffsetMin, context, fontFile);

                var glyphData = new HashSet<GlyphData>(glyphs.Select(x => x.Data));

                // Optimize.
                // FW (2023-01-26): Don't use the optimize.
                //foreach (GlyphData glyph in glyphData)
                //{
                //    GlyphCropper.Crop(glyph);
                //}

                // We need to know how to pack the glyphs.
                bool requiresPot, requiresSquare;
                texProfile.Requirements(context,
                    TextureFormat,
                    out requiresPot,
                    out requiresSquare);

                var face = GlyphPacker.ArrangeGlyphs(glyphData.ToArray(),
                    requiresPot,
                    requiresSquare);

                // Adjust line and character spacing.
                lineSpacing += input.Spacing;
                output.VerticalLineSpacing = (int)lineSpacing;

                foreach (Glyph glyph in glyphs)
                {
                    output.CharacterMap.Add(glyph.Character);

                    var texRect = glyph.Data.Subrect;
                    output.Glyphs.Add(texRect);

                    var cropping = new Rectangle(0,
                        (int)(glyph.Data.YOffset - yOffsetMin),
                        (int)glyph.Data.XAdvance,
                        output.VerticalLineSpacing);
                    output.Cropping.Add(cropping);

                    // Set the optional character kerning.
                    if (input.UseKerning)
                    {
                        ABCFloat widths = glyph.Data.CharacterWidths;
                        output.Kerning.Add(new Vector3(widths.A, widths.B, widths.C));
                    }
                    else
                    {
                        output.Kerning.Add(new Vector3(0, texRect.Width, 0));
                    }
                }

                output.Texture.Faces[0].Add(face);
            }

            if (PremultiplyAlpha)
            {
                var bmp = output.Texture.Faces[0][0];
                var data = bmp.GetPixelData();
                var idx = 0;
                for (; idx < data.Length;)
                {
                    var r = data[idx];

                    // Special case of simply copying the R component into the A, since R is the value of white alpha we want
                    //data[idx + 0] = r; // Not necessary.
                    data[idx + 1] = r;
                    data[idx + 2] = r;
                    data[idx + 3] = r;

                    idx += 4;
                }

                bmp.SetPixelData(data);

                WriteBmpData(input.Identity.SourceFilename, bmp.Width, bmp.Height, data);
            }
            else
            {
                var bmp = output.Texture.Faces[0][0];
                var data = bmp.GetPixelData();
                var idx = 0;
                for (; idx < data.Length;)
                {
                    var r = data[idx];

                    // Special case of simply moving the R component into the A and setting RGB to solid white, since R is the value of white alpha we want
                    data[idx + 0] = 255;
                    data[idx + 1] = 255;
                    data[idx + 2] = 255;
                    data[idx + 3] = r;

                    idx += 4;
                }

                bmp.SetPixelData(data);

                WriteBmpData(input.Identity.SourceFilename, bmp.Width, bmp.Height, data);
            }

            // Perform the final texture conversion.
            texProfile.ConvertTexture(context, output.Texture, TextureFormat, true);

            return output;
        }

        private static void WriteBmpData(string fontFile,
            int width,
            int height,
            byte[] data)
        {
            var dBmp = new DirectBitmap(width, height, data);
            var tmpFile = Path.Combine(Path.GetDirectoryName(fontFile),
                $"{Path.GetFileNameWithoutExtension(fontFile)}.png");
            dBmp.Write(tmpFile);
        }

        private static Glyph[] ImportFont(FontDescription options,
            out float lineSpacing,
            out int yOffsetMin,
            ContentProcessorContext context,
            string fontName)
        {
            // Which importer knows how to read this source font?
            IFontImporter importer;

            var TrueTypeFileExtensions = new List<string> { ".ttf", ".ttc", ".otf" };
            //var BitmapFileExtensions = new List<string> { ".bmp", ".png", ".gif" };

            string fileExtension = Path.GetExtension(fontName).ToLowerInvariant();

            //			if (BitmapFileExtensions.Contains(fileExtension))
            //			{
            //				importer = new BitmapImporter();
            //			}
            //			else
            //			{
            if (!TrueTypeFileExtensions.Contains(fileExtension))
                throw new PipelineException("Unknown file extension " + fileExtension);

            importer = new SharpFontImporter();

            // Import the source font data.
            var faceHeight = importer.Import(options, fontName);

            lineSpacing = importer.LineSpacing;
            yOffsetMin = importer.YOffsetMin;

            // Get all glyphs
            var glyphs = new List<Glyph>(importer.Glyphs);

            // Validate.
            if (glyphs.Count == 0)
            {
                throw new Exception("Font does not contain any glyphs.");
            }

            // Sort the glyphs
            glyphs.Sort((left, right) => left.Character.CompareTo(right.Character));


            // Check that the default character is part of the glyphs
            if (options.DefaultCharacter != null)
            {
                bool defaultCharacterFound = false;
                foreach (var glyph in glyphs)
                {
                    if (glyph.Character == options.DefaultCharacter)
                    {
                        defaultCharacterFound = true;
                        break;
                    }
                }
                if (!defaultCharacterFound)
                {
                    throw new InvalidOperationException("The specified DefaultCharacter is not part of this font.");
                }
            }

            return glyphs.ToArray();
        }

        private string FindFont(string name, FontDescriptionStyle style, string fontDir)
        {
            // FW (15.09.2022):
            // If the name is a file name, search the file in the special fonts directory.
            var isFileName = Path.HasExtension(name);
            //Console.WriteLine($"name: {name}");
            //Console.WriteLine($"isFileName: {isFileName}");

            if (isFileName)
            {
                var ext = Path.GetExtension(name).ToLower();
                if (ext == ".ttf" || ext == ".otf")
                {
                    if (!string.IsNullOrEmpty(fontDir) &&
                        Directory.Exists(fontDir))
                    {
                        foreach (var filename in Directory.GetFiles(fontDir))
                        {
                            if (Path.GetFileName(filename) == name)
                            {
                                return filename;
                            }
                        }
                    }
                }
                return String.Empty;
            }

            if (CurrentPlatform.OS == OS.Windows)
            {
                // FW (12.09.2022):
                // Remove the regular bit, to get the right serialized string later.
                // Also remove strikeout and underline, because this should be done
                // by the drawing library (SharpFont).
                style &= ~FontDescriptionStyle.Regular;
                style &= ~FontDescriptionStyle.Underline;
                style &= ~FontDescriptionStyle.Strikeout;

                // FW (12.09.2022):
                // Create the file name. Use also the style to get the right file.
                if (style != FontDescriptionStyle.Regular)
                {
                    var stylestr = style.ToString().Replace(",", string.Empty);
                    name = $"{name} {stylestr}".Trim();
                }
                //Console.WriteLine($"name: {name}");

                var fontDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
                foreach (var key in new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    var subkey = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts",
                        false);
                    foreach (var font in subkey.GetValueNames().OrderBy(x => x))
                    {
                        string retValue = null;
                        if (isFileName)
                        {
                            var fontPath = subkey.GetValue(font).ToString();
                            //Console.WriteLine($"Compare font: {fontPath}- with name:{name}");
                            if (name == fontPath)
                            {
                                retValue = fontPath;
                            }
                        }
                        else
                        {
                            //Console.WriteLine($"Compare font: {font}- with name:{name}");
                            if (font.StartsWith(name,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                retValue = subkey.GetValue(font).ToString();
                            }
                        }

                        if (!string.IsNullOrEmpty(retValue))
                        {
                            // The registry value might have trailing NUL characters
                            // See https://github.com/MonoGame/MonoGame/issues/4061
                            var nulIndex = retValue.IndexOf('\0');
                            if (nulIndex != -1)
                                retValue = retValue.Substring(0, nulIndex);

                            return Path.IsPathRooted(retValue) ?
                                retValue : Path.Combine(fontDirectory, retValue);
                        }
                    }
                }
            }
            else if (CurrentPlatform.OS == OS.Linux)
            {
                string s, e;
                ExternalTool.Run("/bin/bash", string.Format("-c \"fc-match -f '%{{file}}:%{{family}}\\n' '{0}:style={1}'\"", name, style), out s, out e);
                s = s.Trim();

                var split = s.Split(':');
                if (split.Length < 2)
                    return string.Empty;

                // check font family, fontconfig might return a fallback
                if (split[1].Contains(","))
                {
                    // this file defines multiple family names
                    var families = split[1].Split(',');
                    foreach (var f in families)
                    {
                        if (f.ToLowerInvariant() == name.ToLowerInvariant())
                            return split[0];
                    }
                    // didn't find it
                    return string.Empty;
                }
                else
                {
                    if (split[1].ToLowerInvariant() != name.ToLowerInvariant())
                        return string.Empty;
                }

                return split[0];
            }

            return String.Empty;
        }
    }
}
