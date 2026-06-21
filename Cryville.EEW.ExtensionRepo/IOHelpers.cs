using System.IO;

namespace Cryville.EEW.ExtensionRepo {
	static class IOHelpers {
		public static void CopyDirectoryOverriddingOlder(string sourceDir, string destinationDir, bool recursive) {
			var dir = new DirectoryInfo(sourceDir);
			if (!dir.Exists)
				throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}.");
			Directory.CreateDirectory(destinationDir);

			foreach (var file in dir.GetFiles()) {
				string targetFilePath = Path.Combine(destinationDir, file.Name);
				var targetFile = new FileInfo(targetFilePath);
				if (targetFile.Exists && file.LastWriteTimeUtc <= targetFile.LastWriteTimeUtc) {
					continue;
				}
				file.CopyTo(targetFilePath, true);
			}
			if (recursive) {
				foreach (var subDir in dir.GetDirectories()) {
					string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
					CopyDirectoryOverriddingOlder(subDir.FullName, newDestinationDir, true);
				}
			}
		}
	}
}
