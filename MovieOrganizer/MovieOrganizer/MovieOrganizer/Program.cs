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

			//var sourceDirectory = new DirectoryInfo(@"\\10.0.0.48\Plex Media\Porn");
			//var resultDirectory = new DirectoryInfo(@"\\10.0.0.48\Plex Media\Porn2");

			//var sourceDirectory = new DirectoryInfo(@"C:\Xegster\Internet Images\NSFW\Aloha Downloads");
			var sourceDirectory = new DirectoryInfo(@"\\10.0.0.48\Hoarding\NSFW\Models");
			var resultDirectory = new DirectoryInfo(@"C:\Xegster\Internet Images\NSFW\SortedAloha");


			var outputDirectory = new DirectoryInfo(@"C:\Xegster\Internet Images\NSFW");

			//OrganizeMoviesByActress(sourceDirectory, resultDirectory);
			var count = DeleteEmptyFolders(sourceDirectory);
			Console.Write(string.Format("Deleted {0} empty folders.", count));
			Console.ReadLine();
			//OutputListOfFiles(@"\\10.0.0.48\Plex Media\Porn");
			//OutputListOfFiles(sourceDirectory, outputDirectory);
		}

		private static int DeleteEmptyFolders(DirectoryInfo sourceDirectory)
		{
			var dirs = sourceDirectory.GetDirectories("*", SearchOption.TopDirectoryOnly);
			int total = 0;
			if (dirs.Count() > 0)
			{
				foreach (var dir in dirs)
				{
					total += DeleteEmptyFolders(dir);
				}
			}

			var files = sourceDirectory.GetFiles("*", SearchOption.TopDirectoryOnly);
			dirs = sourceDirectory.GetDirectories("*", SearchOption.TopDirectoryOnly);
			if (files.Count() == 0 && dirs.Count() == 0)
			{
				Console.WriteLine(string.Format("Deleting \n{0}", sourceDirectory.FullName));
				sourceDirectory.Delete();
				return total + 1;
			}
			else return total;
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

		private static void OutputListOfFiles(DirectoryInfo targetDirectory, DirectoryInfo outputDir)
		{
			//DirectoryInfo di = new DirectoryInfo(directoryPath);
			//var curDir = @"C:\Xegster\Development\Test";//Directory.GetCurrentDirectory();
			var allFiles = targetDirectory.GetFiles("*", SearchOption.AllDirectories);
			var allXegFiles = allFiles.Select(af => new XegFileInfo(af)).ToList();
			var allFileNames = allXegFiles.Select(af => af.FileName).OrderBy(af => af.Length).ToList();
			var allFileNamesAndFolder = allXegFiles.Select(axf => string.Format("{0}\t{1}\t{2}\t{3}", axf.Quality.ToString(), axf.ProbableFolder, axf.FileNameWithoutExtension, axf.FullName));
			var outputFilePath = Path.Combine(outputDir.FullName, "Output.txt");
			var header = string.Format("{0}\t{1}\t{2}\t{3}", "Quality", "Folder", "File Name", "Path");
			var outputText = header + "\n" + string.Join("\n", allFileNamesAndFolder);

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
