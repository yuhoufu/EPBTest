using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataOperation
{
  public  class ClsDiskProc
    {
        public static string MakeSubDir(string MainPath)
        {
            try
            {
                if (!Directory.Exists(MainPath))
                {
                    Directory.CreateDirectory(MainPath);
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

        }

        public static  long GetHardDiskSpace(string str_HardDiskName)
        {
            long totalSize = 0;
            str_HardDiskName = str_HardDiskName.ToUpper() + "\\";
            DriveInfo[] drives = DriveInfo.GetDrives();
            foreach (DriveInfo drive in drives)
            {
                if (drive.Name == str_HardDiskName)
                {
                    totalSize = drive.TotalFreeSpace / (1024 * 1024 * 1024);
                }
            }
            return totalSize;
        }


        public static string CopyConfigFile(string sourceDirectory, string SourceFile,string destinationDirectory)
        {
            try
            {
               
                string sourceFilePath = Path.Combine(sourceDirectory, SourceFile);
                string destinationFilePath = Path.Combine(destinationDirectory, SourceFile);

                if (File.Exists(sourceFilePath))
                {
                    if (!Directory.Exists(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    File.Copy(sourceFilePath, destinationFilePath, true);
                    return "OK";
                }
                else
                {
                    return $"源文件 {sourceFilePath} 不存在";
                }
            }
            catch (Exception ex)
            {
                return $"复制文件时出现错误: {ex.Message}";
            }
        }



    }
}
