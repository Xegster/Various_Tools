using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MovieOrganizer
{
	public class Program
	{
		public static void Main(string[] args)
		{
			//var sourceDirectory = args != null && args.Length > 0 ? args[0] : "";
			//var resultDirectory = args != null && args.Length > 1 ? args[1] : "";
			//var sourceDirectory = new DirectoryInfo(@"\\10.0.0.48\Hoarding\NSFW\Porn\Test");
			//var resultDirectory = new DirectoryInfo(@"\\10.0.0.48\Hoarding\NSFW\Porn\Test2");

			var sourceDirectory = new DirectoryInfo(@"\\10.0.0.48\Plex Media\Porn");
			var resultDirectory = new DirectoryInfo(@"\\10.0.0.48\Plex Media\Porn2");


			//OrganizeMoviesByActress(sourceDirectory, resultDirectory);

			//OutputListOfFiles(@"\\10.0.0.48\Plex Media\Porn");
			OutputListOfFiles(sourceDirectory);
		}

		private static void OrganizeMoviesByActress(DirectoryInfo sourceDirectory, DirectoryInfo resultDirectory)
		{
			var allFiles = sourceDirectory.GetFiles("*", SearchOption.AllDirectories);
			var allXegFiles = allFiles.Select(af => new XegFileInfo(af)).ToList();
			foreach (var xegFile in allXegFiles)
			{
				var targetDir = Path.Combine(resultDirectory.FullName, xegFile.ProbableFolder);
				if (xegFile.Viewed)
					targetDir = Path.Combine(targetDir, "Viewed");
				EnsureDirectory(targetDir);
				var targetPath = Path.Combine(targetDir, xegFile.FileName);
				File.Move(xegFile.FullName, targetPath);
			}

		}

		public static void EnsureDirectory(string dir)
		{
			if (!Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
			}
		}

		private static void OutputListOfFiles(DirectoryInfo directory)
		{
			//DirectoryInfo di = new DirectoryInfo(directoryPath);
			var curDir = @"C:\Xegster\Development\Test";//Directory.GetCurrentDirectory();
			var allFiles = directory.GetFiles("*", SearchOption.AllDirectories);
			var allXegFiles = allFiles.Select(af => new XegFileInfo(af)).ToList();
			var allFileNames = allXegFiles.Select(af => af.FileName).OrderBy(af => af.Length).ToList();
			var allFileNamesAndFolder = allXegFiles.Select(axf => string.Format("{0}\t{1}\t{2}\t{3}", axf.Quality.ToString(), axf.ProbableFolder, axf.FileNameWithoutExtension, axf.FullName));
			var outputFilePath = Path.Combine(curDir, "Output.txt");
			var outputText = string.Join("\n", allFileNamesAndFolder);

			AddToFile(outputFilePath, outputText, false);

		}

		private static void AddToFile(string file, string content, bool append)
		{
			try
			{
				if (!Directory.Exists(Path.GetDirectoryName(file)))
					Directory.CreateDirectory(Path.GetDirectoryName(file));
				using (StreamWriter sr = new StreamWriter(file, append))
				{
					sr.WriteLine(content);
				}
			}
			catch { }
		}
	}
}
