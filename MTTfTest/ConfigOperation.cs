using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Configuration;

namespace MtEmbTest
{
    class ConfigOperation
    {
        public static void addItem(string keyName, string keyValue)
        {
            //添加配置文件的项，键为keyName，值为keyValue
            Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            config.AppSettings.Settings.Add(keyName, keyValue);
            config.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("configuration");

        }
        //判断键为keyName的项是否存在：
        public static bool existItem(string keyName)
        {
            //判断配置文件中是否存在键为keyName的项
            foreach (string key in ConfigurationManager.AppSettings)
            {
                if (key == keyName)
                {
                    //存在
                    return true;
                }
            }
            return false;
        }
        //获取键为keyName的项的值：
        public static string valueItem(string keyName)
        {
            //返回配置文件中键为keyName的项的值
            return ConfigurationManager.AppSettings[keyName];
        }
        //修改键为keyName的项的值：
        public static void modifyItem(string keyName, string newKeyValue)
        {
            //修改配置文件中键为keyName的项的值
            Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            config.AppSettings.Settings[keyName].Value = newKeyValue;
            config.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }
        //删除键为keyName的项：
        public static void removeItem(string keyName)
        {
            //删除配置文件键为keyName的项
            Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            config.AppSettings.Settings.Remove(keyName);
            config.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }

        public static string SetOneItem(string ItemName)
        {
            if (!existItem(ItemName))
            {
               
                return "999";
            }

            return valueItem(ItemName);
        }

        public static void SaveOneItem(string ItemName, string ItemValue)
        {
            if (ItemValue.Length < 1)
            {
                return;
            }

            //如果输入的是空值就返回
            try
            {
                if (existItem(ItemName))
                {
                    modifyItem(ItemName, ItemValue);
                }
                else
                {
                    addItem(ItemName, ItemValue);
                }
            }
            catch (Exception ex)
            {
                throw;
            }
        }
    }
}
