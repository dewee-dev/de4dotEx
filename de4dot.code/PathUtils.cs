/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace de4dot.code {
	// Builds safe output paths for files whose names come from the input file
	static class PathUtils {
		const int MaxEmbeddedNameLength = 200;
		const int MaxEmbeddedExtensionLength = 16;
		const int MaxEmbeddedNameSuffix = 1000;

		// Returns a full path directly inside outDir for an embedded file, or null if no unused
		// name was found. Only a plain file name is kept from assemblyName and extension, so the
		// result can't escape outDir (eg. "../x", "/abs/x", "C:\\x"). An existing file is never
		// overwritten: a numeric suffix is added instead (name_1.dll, name_2.dll, ...). A name
		// that is empty or still unusable becomes embedded_N.
		public static string GetEmbeddedFilePath(string outDir, string assemblyName, string extension, ref int counter) {
			if (string.IsNullOrEmpty(outDir))
				outDir = ".";	// NewFilename has no directory part
			var baseDir = Path.GetFullPath(outDir);
			var baseDirPrefix = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			var name = SanitizeEmbeddedName(assemblyName);
			var ext = SanitizeEmbeddedExtension(extension);
			if (name.Length != 0) {
				var path = GetUnusedFilePath(baseDirPrefix, name, ext);
				if (path != null)
					return path;
			}
			return GetUnusedFilePath(baseDirPrefix, "embedded_" + (++counter).ToString(CultureInfo.InvariantCulture), ext);
		}

		// Returns null if name + ext isn't a file directly in the directory or all suffixes are taken
		static string GetUnusedFilePath(string baseDirPrefix, string name, string ext) {
			var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
			for (int i = 0; i <= MaxEmbeddedNameSuffix; i++) {
				var fileName = i == 0 ? name + ext : name + "_" + i.ToString(CultureInfo.InvariantCulture) + ext;
				var path = Path.GetFullPath(Path.Combine(baseDirPrefix, fileName));
				if (path.Length <= baseDirPrefix.Length || !path.StartsWith(baseDirPrefix, StringComparison.Ordinal) ||
					path.IndexOfAny(separators, baseDirPrefix.Length) >= 0)
					return null;
				if (!File.Exists(path) && !Directory.Exists(path))
					return path;
			}
			return null;
		}

		// Returns "" if nothing usable is left (eg. "", "." or "..")
		static string SanitizeEmbeddedName(string name) {
			name = ToPlainFileName(name);
			if (name.Length > MaxEmbeddedNameLength) {
				name = name.Substring(0, MaxEmbeddedNameLength);
				if (char.IsHighSurrogate(name[name.Length - 1]))
					name = name.Substring(0, name.Length - 1);
			}
			// Windows drops trailing dots and spaces
			name = name.TrimEnd('.', ' ');
			if (name.Length == 0)
				return string.Empty;
			if (IsReservedDeviceName(name))
				name = "_" + name;
			return name;
		}

		// Returns "" or "." followed by at least one char that isn't a dot
		static string SanitizeEmbeddedExtension(string extension) {
			var ext = ToPlainFileName(extension).TrimStart('.').Replace('.', '_').Trim();
			if (ext.Length > MaxEmbeddedExtensionLength)
				ext = ext.Substring(0, MaxEmbeddedExtensionLength);
			if (ext.Length == 0)
				return string.Empty;
			return "." + ext;
		}

		// Keeps the last path component and replaces chars that are invalid in a file name on
		// any supported OS
		static string ToPlainFileName(string name) {
			if (string.IsNullOrEmpty(name))
				return string.Empty;
			int index = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
			name = name.Substring(index + 1);
			var invalid = Path.GetInvalidFileNameChars();
			var sb = new StringBuilder(name.Length);
			foreach (var c in name)
				sb.Append(c < 0x20 || "\"*:<>?|".IndexOf(c) >= 0 || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
			return sb.ToString().Trim();
		}

		static bool IsReservedDeviceName(string name) {
			int index = name.IndexOf('.');
			var baseName = (index < 0 ? name : name.Substring(0, index)).TrimEnd(' ').ToUpperInvariant();
			switch (baseName) {
			case "CON": case "PRN": case "AUX": case "NUL": case "CONIN$": case "CONOUT$":
				return true;
			}
			return baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.Ordinal) || baseName.StartsWith("LPT", StringComparison.Ordinal)) &&
				baseName[3] >= '0' && baseName[3] <= '9';
		}
	}
}
