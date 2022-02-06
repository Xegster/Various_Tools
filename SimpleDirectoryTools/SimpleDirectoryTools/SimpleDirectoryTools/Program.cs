using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleDirectoryTools
{
	public class Program
	{
		public static void Main(string[] args)
		{
			var curDir = Environment.CurrentDirectory;
			DeleteEmptyDirs(curDir);
		}
		public static void DeleteEmptyDirs(string dir)
		{

			try
			{
				foreach (var d in Directory.EnumerateDirectories(dir))
				{
					DeleteEmptyDirs(d);
				}

				var entries = Directory.EnumerateFileSystemEntries(dir);

				if (!entries.Any())
				{
					try
					{
						Directory.Delete(dir);
					}
					catch (UnauthorizedAccessException) { }
					catch (DirectoryNotFoundException) { }
				}
			}
			catch (UnauthorizedAccessException) { }
		}
	}
}
