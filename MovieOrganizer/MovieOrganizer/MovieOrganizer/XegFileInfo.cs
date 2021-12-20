using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MovieOrganizer
{
	public class XegFileInfo
	{
		public FileInfo FileInfo { get; set; }

		public string[] Keywords { get; set; }
		public Quality Quality { get; set; }
		public bool Viewed { get; set; }
		public string FileName { get { return FileInfo.Name; } }
		public string FileNameWithoutExtension { get { return Path.GetFileNameWithoutExtension(FileInfo.Name); } }
		public string FullName { get { return FileInfo.FullName; } }
		private List<Quality> _allQualitites;
		private List<Quality> AllQualities
		{
			get
			{
				if (_allQualitites == null)
					_allQualitites = Enum.GetValues(typeof(Quality)).Cast<Quality>().Skip(1).ToList();
				return _allQualitites;
			}
		}
		public string ProbableFolder
		{
			get
			{
				if (Keywords == null || Keywords.Length == 0)
					return "Unsorted";
				else if (Keywords.Length == 1)
					return Keywords.First();
				else
					return Keywords.Last();

				//if (Keywords == null || Keywords.Length <= 1)
				//{
				//	return "Unsorted";
				//}
				//else if (Keywords.Length == 2 && IsQualityToken(Keywords[1]))
				//{
				//	return "Unsorted";
				//}
				//else if (Keywords.Length == 2)
				//{
				//	return Keywords[1];
				//}
				//else
				//{
				//	return Keywords[Keywords.Length - 2];
				//}

			}
		}
		private bool IsQualityToken(string token)
		{
			foreach (var value in AllQualities)
				if (token.ToLower().Contains(value.ToString().ToLower()))
					return true;
			return false;
		}
		public XegFileInfo(FileInfo someFile)
		{
			FileInfo = someFile;
			//var fileNameNoNumbers = Regex.Replace(FileNameWithoutExtension, @"[\d]+", "");
			var possibles = FileNameWithoutExtension.Split(new char[] { '.', '_', '(', ')', ' ' });

			var noTokensWithNumbers = possibles.Where(p => !Regex.IsMatch(p, @"[\d]+")).ToArray();
			var excludeCertainTokens = noTokensWithNumbers.Where(p => p.ToLower() != "com" && p.ToLower() != "master" && p.ToLower() != "xvideos").ToArray();
			var noQualityTokens = excludeCertainTokens.Where(p => !IsQualityToken(p)).ToArray();
			var noEmpty = noQualityTokens.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
			var trimmed = noEmpty.Select(ne => ne.Trim()).ToArray();
			Keywords = SplitByCasing(trimmed);
			if (Keywords == null || Keywords.Length == 0)
				Keywords = CheckSpecialCases();


			foreach (var value in AllQualities)
			{
				var exists = noTokensWithNumbers.FirstOrDefault(p => p.ToLower().Contains(value.ToString().ToLower()));
				if (exists != null)
				{
					Quality = value;
					break;
				}
			}
			if (Quality == Quality.NA)
			{
				var path = someFile.Directory.FullName.ToLower();
				foreach (var value in AllQualities)
				{
					if (path.Contains(value.ToString().ToLower()))
					{
						Quality = value;
						break;
					}
				}

			}
			Viewed = FullName.ToLower().Contains("viewed") || Quality != Quality.NA;

		}

		private string[] CheckSpecialCases()
		{
			if (FileNameWithoutExtension.StartsWith("xvideos.com_"))
			{
				var workingCopy = FileNameWithoutExtension.Replace("xvideos.com_", "");
				if (workingCopy.StartsWith("."))
					workingCopy = workingCopy.Substring(1);
				int endIndex = -1;
				for (int i = 0; i < workingCopy.Length; i++)
				{
					if (!char.IsLetter(workingCopy[i]))
					{
						endIndex = i;
						break;
					}
				}
				if (endIndex >= 4)
				{
					workingCopy = workingCopy.Remove(endIndex);
					return SplitByCasing(new string[1] { workingCopy });
				}
			}
			return new string[0];
		}

		private string[] SplitByCasing(string[] noQualityTokens)
		{
			if (noQualityTokens == null || noQualityTokens.Length == 0)
				return new string[0];
			List<string> ret = new List<string>();
			foreach (var token in noQualityTokens)
			{
				if (token.ToLower() == token)
				{
					ret.Add(token);
					continue;
				}
				else
				{
					string newToken = "";
					for (int i = 0; i < token.Length; i++)
					{
						if (i == 0)
						{
							newToken += token[i];
						}
						else if (char.IsLower(token[i]))
						{
							newToken += token[i];
						}
						else
						{
							newToken += " " + token[i];
						}
					}
					ret.Add(newToken);
				}
			}
			return ret.ToArray();
		}
	}

	public enum Quality
	{
		NA = 0,
		Good = 1,
		Great = 2,
		SoSo = 3,
		Best = 4
	}
}
