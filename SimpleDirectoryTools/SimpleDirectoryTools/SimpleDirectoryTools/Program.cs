using System;
using System.IO;
using System.Linq;

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
						Console.WriteLine("Deleting: " + dir);
						Directory.Delete(dir);
					}
					catch (UnauthorizedAccessException)
					{
						Console.WriteLine("No Access!");
					}
					catch (DirectoryNotFoundException)
					{
						Console.WriteLine("Directory Not Found!");
					}
				}
			}
			catch (UnauthorizedAccessException) { }
		}
	}
}
