using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Folder_Renaming
{
	public class Program
	{
		public static string DirToSearch
		{
			get { return @"Z:\Shared Videos\"; }
			set { }
		}
		public static string CompletedFilesOutputFile { get { return DirToSearch + "\\CompletedFiles.txt"; } }
		public static string CompletedDirectoriesOutputFile { get { return DirToSearch + "\\CompletedDirectories.txt"; } }
		public static string GenericOutput { get { return DirToSearch + "\\GeneralStuff.txt"; } }
		public static void Main(string[] args)
		{
			Console.WriteLine("Code can be found in the CloudMovieManagement solution of the Various Tools repo stored on GitHub for the Xegster account. ");
			Console.WriteLine("What to do?" +
				"\n1. Analyze and fix file/folder names here." +
				"\n2. Fix year format here." +
				"\n3. Output All Extensions.");
			var answerKey = Console.ReadKey();
			var answer = answerKey.KeyChar;

			switch (answer)
			{
				case '1':
					AnalyzeAndFixFoldersAndFiles();
					break;
				case '2':
					FormatYearTokenInThisDirectory();
					break;
				case '3':
					OutputAllExtensions();
					break;
			}
			//OutputAllExtensions();


		}

		private static void FormatYearTokenInThisDirectory()
		{
			DirToSearch = Directory.GetCurrentDirectory();
			var files = Directory.GetFiles(DirToSearch);
			var directories = Directory.GetDirectories(DirToSearch);
			foreach (var filePath in files)
				FormatYearToken(filePath, true);
			foreach (var dirPath in directories)
				FormatYearToken(dirPath, false);
		}

		private static void FormatYearToken(string path, bool isFile)
		{
			var name = isFile ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
			var split = name.Split(new char[] { ' ' });
			var finalToken = split[split.Length - 1];
			if (finalToken.StartsWith("(") && finalToken.EndsWith(")"))
				return;
			finalToken = finalToken.Replace("(", "").Replace(")", "");
			int trash;
			if (!int.TryParse(finalToken, out trash) || trash < 1920 || trash > 2050)
				return;

			var newFileName = string.Join(" ", split.Take(split.Length - 1)) + string.Format(" ({0})", finalToken);

			if (isFile)
				RenameFile(path, newFileName);
			else RenameDirectory(path, newFileName);

		}

		private static void AnalyzeAndFixFoldersAndFiles()
		{
			DirToSearch = Directory.GetCurrentDirectory();
			var completedFiles = File.Exists(CompletedFilesOutputFile) ? File.ReadAllLines(CompletedFilesOutputFile).ToList() : new List<string>();
			var completedDirectories = File.Exists(CompletedDirectoriesOutputFile) ? File.ReadAllLines(CompletedDirectoriesOutputFile).ToList() : new List<string>();

			var files = Directory.GetFiles(DirToSearch).Except(completedFiles).ToList();
			var directories = Directory.GetDirectories(DirToSearch).Except(completedDirectories).ToList();
			foreach (var filePath in files)
				FixPathAlgorithm(filePath, true);
			foreach (var dirPath in directories)
				FixPathAlgorithm(dirPath, false);
		}

		private static void FixPathAlgorithm(string path, bool isFile)
		{
			var name = isFile ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
			if (string.IsNullOrWhiteSpace(name))
			{
				AddToFile(isFile ? CompletedFilesOutputFile : CompletedDirectoriesOutputFile, path);
				return;
			}
			var revisedName = FixFileName(name, FixAlgorithm.BASIC);
			if (revisedName.Trim() != name.Trim())
			{
				FixAlgorithm result = GetUserInput(name, revisedName, isFile);
				while (result != FixAlgorithm.FIXED && result != FixAlgorithm.SKIP && result != FixAlgorithm.IGNORE)
				{
					revisedName = FixFileName(name, result);
					result = GetUserInput(name, revisedName, isFile);
				}
				if (result == FixAlgorithm.SKIP)
					return;
				else if (result == FixAlgorithm.IGNORE)
					AddToFile(isFile ? CompletedFilesOutputFile : CompletedDirectoriesOutputFile, path);
				else if (result == FixAlgorithm.FIXED)
				{
					string newFilePath = isFile ? RenameFile(path, revisedName) : RenameDirectory(path, revisedName);
					AddToFile(isFile ? CompletedFilesOutputFile : CompletedDirectoriesOutputFile, newFilePath);
				}
			}
		}

		private static string RenameFile(string filePath, string revisedName)
		{
			var newFilePath = Path.GetDirectoryName(filePath) + Path.DirectorySeparatorChar + revisedName + Path.GetExtension(filePath);
			File.Move(filePath, newFilePath);
			return newFilePath;
		}
		private static string RenameDirectory(string dirPath, string revisedName)
		{
			var newFilePath = Path.GetDirectoryName(dirPath) + Path.DirectorySeparatorChar + revisedName;
			if (dirPath != newFilePath)
			{
				if (dirPath.ToLower() == newFilePath.ToLower())
				{
					var tempFilePath = Path.GetDirectoryName(dirPath) + Path.DirectorySeparatorChar + "TRASH_OMG_" + Guid.NewGuid().ToString();
					Directory.Move(dirPath, tempFilePath);
					dirPath = tempFilePath;
				}
				//TODO: Track duplicate movies somewhere
				try
				{
					Directory.Move(dirPath, newFilePath);
				}
				catch
				{
					var extFreeName = Path.GetFileNameWithoutExtension(revisedName);
					var modName = revisedName.Replace(extFreeName, extFreeName + "(1)");
					newFilePath = Path.GetDirectoryName(dirPath) + Path.DirectorySeparatorChar + modName;
					try
					{
						Directory.Move(dirPath, newFilePath);
					}
					catch
					{
						Console.WriteLine("Duplicate movie!"); return dirPath;
					}
				}
			}
			return newFilePath;
		}
		private static FixAlgorithm GetUserInput(string fileName, string revisedName, bool isFile)
		{
			string buildMessage = string.Format("Replace this {2}:\n{0}\nWith this one?\n{1}\n", fileName, revisedName, isFile ? "file" : "directory");
			buildMessage += "\n1. 1/y for yes, replace.";
			buildMessage += "\n2. 2/s for skip.";
			buildMessage += "\n3. 3/r for manual rename.";
			buildMessage += "\n4. 4/i to ignore forever.";
			buildMessage += "\n5. 5/q if this is a sequel.";
			Console.WriteLine(buildMessage);
			var answerInfo = Console.ReadKey();
			var answer = answerInfo.KeyChar.ToString().ToLower().ToArray()[0];
			switch (answer)
			{
				case '1':
				case 'y':
					return FixAlgorithm.FIXED;
				case '2':
				case 's':
					return FixAlgorithm.SKIP;
				case '3':
				case 'r':
					return FixAlgorithm.MANUAL_RENAME;
				case '4':
				case 'i':
					return FixAlgorithm.IGNORE;
				case '5':
				case 'q':
					return FixAlgorithm.IS_SEQUEL;
				default:
					return FixAlgorithm.SKIP;
			}
		}

		private static void OutputAllExtensions()
		{
			DirToSearch = Directory.GetCurrentDirectory();
			var files = Directory.GetFiles(DirToSearch, "*", SearchOption.AllDirectories);
			var extensions = files.Select(file => Path.GetExtension(file)).Distinct().ToArray();
			AddToFile(GenericOutput, string.Join("\n", extensions));
		}

		private enum FixAlgorithm
		{
			FIXED,
			SKIP,
			BASIC,
			MANUAL_RENAME,
			IGNORE,
			IS_SEQUEL,
		}
		private static string FixFileName(string fileName, FixAlgorithm fa)
		{
			switch (fa)
			{
				case FixAlgorithm.SKIP:
				case FixAlgorithm.FIXED:
					return fileName;
				case FixAlgorithm.BASIC:
					return BasicFileNameFix(fileName, 1);
				case FixAlgorithm.IS_SEQUEL:
					return BasicFileNameFix(fileName, 2);
				case FixAlgorithm.MANUAL_RENAME:
					Console.WriteLine("\nType the full name:");
					string read = Console.ReadLine();
					return read;
				default:
					return "ERROR";
			}
		}

		private static string BasicFileNameFix(string fileName, int numeralCount)
		{
			string noDots = fileName.Replace(".", " ").Replace("_", " ");
			var split = noDots.Split(new char[] { ' ' });
			if (split.Length == 1)
				return noDots;
			TextInfo txtInfo = new CultureInfo("en-us", false).TextInfo;
			string ret = txtInfo.ToTitleCase(split[0].ToLower()) + " ";
			int trash = -1;
			int count = 0;
			ret += string.Join(" ", split.Skip(1).TakeWhile(sp =>
			{
				if (count == numeralCount) return false;
				else if (int.TryParse(sp.Replace("p", "").Replace("(", "").Replace(")", ""), out trash))
				{
					count++;
					return true;
				}
				else return true;
			}).Select(sp =>
			{
				var temp = sp.ToLower();
				if (temp == "a" || temp == "and" || temp == "the" || temp == "in" || temp == "of" || temp == "to" || temp == "go" || temp == "at")
					return temp;
				else if (Regex.IsMatch(temp.ToUpper(), "^M{0,4}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$"))
					return temp.ToUpper();
				else
					return txtInfo.ToTitleCase(temp);
			}).ToArray());
			return ret;
		}

		public static void AddToFile(string file, string content)
		{
			try
			{
				if (!Directory.Exists(Path.GetDirectoryName(file)))
					Directory.CreateDirectory(Path.GetDirectoryName(file));
				using (StreamWriter sr = new StreamWriter(file, true))
				{
					sr.WriteLine(content);
				}
			}
			catch { }
		}

	}
}
