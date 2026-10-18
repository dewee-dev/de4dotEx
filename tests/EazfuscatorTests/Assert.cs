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
using System.IO;
using System.Text;

namespace de4dot.tests {
	/// <summary>断言失败：算作测试失败。</summary>
	sealed class AssertException : Exception {
		public AssertException(string message) : base(message) { }
	}

	/// <summary>当前平台跑不了这个场景，跳过而不是失败。</summary>
	sealed class SkipException : Exception {
		public SkipException(string message) : base(message) { }
	}

	static class Assert {
		public static void True(bool condition, string message) {
			if (!condition)
				throw new AssertException(message);
		}

		public static void False(bool condition, string message) => True(!condition, message);

		public static void Contains(string text, string expected, string what) {
			if (text == null || text.IndexOf(expected, StringComparison.Ordinal) < 0)
				throw new AssertException(what + ": expected to find \"" + expected + "\"");
		}

		public static void NotContains(string text, string unexpected, string what) {
			if (text != null && text.IndexOf(unexpected, StringComparison.Ordinal) >= 0)
				throw new AssertException(what + ": didn't expect \"" + unexpected + "\"");
		}

		/// <summary>
		/// 程序集里是否有这个标识符。元数据字符串堆是 UTF-8，字符串字面量是 UTF-16，
		/// 两种编码都查一遍，断言就不用关心目标落在哪个堆里。
		/// </summary>
		public static bool FileContainsText(string path, string text) {
			var bytes = File.ReadAllBytes(path);
			return IndexOf(bytes, Encoding.UTF8.GetBytes(text)) >= 0 ||
				IndexOf(bytes, Encoding.Unicode.GetBytes(text)) >= 0;
		}

		static int IndexOf(byte[] haystack, byte[] needle) {
			if (needle.Length == 0 || haystack.Length < needle.Length)
				return -1;
			int last = haystack.Length - needle.Length;
			for (int i = 0; i <= last; i++) {
				int j = 0;
				while (j < needle.Length && haystack[i + j] == needle[j])
					j++;
				if (j == needle.Length)
					return i;
			}
			return -1;
		}
	}
}
