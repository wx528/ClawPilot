using System.IO;
using Microsoft.Extensions.Logging;

namespace ClawPilot.App;

public class ProfileService
{
    private readonly string _profilesDir;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(ILogger<ProfileService> logger)
    {
        _profilesDir = App.ProfilesDir;
        _logger = logger;
    }

    public string[] GetProfiles()
    {
        try
        {
            if (!Directory.Exists(_profilesDir))
                return Array.Empty<string>();

            return Directory.GetFiles(_profilesDir, "*.yaml")
                .Concat(Directory.GetFiles(_profilesDir, "*.yml"))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => x != null)
                .Select(x => x!)
                .OrderBy(x => x)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取配置文件列表失败");
            return Array.Empty<string>();
        }
    }

    public string? GetProfileContent(string profileName)
    {
        try
        {
            var yamlPath = Path.Combine(_profilesDir, $"{profileName}.yaml");
            if (!File.Exists(yamlPath))
            {
                yamlPath = Path.Combine(_profilesDir, $"{profileName}.yml");
                if (!File.Exists(yamlPath))
                {
                    return null;
                }
            }

            return File.ReadAllText(yamlPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取配置文件 {Profile} 失败", profileName);
            return null;
        }
    }

    public bool SaveProfile(string profileName, string content)
    {
        try
        {
            if (!Directory.Exists(_profilesDir))
                Directory.CreateDirectory(_profilesDir);

            var yamlPath = Path.Combine(_profilesDir, $"{profileName}.yaml");
            File.WriteAllText(yamlPath, content);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置文件 {Profile} 失败", profileName);
            return false;
        }
    }

    public bool DeleteProfile(string profileName)
    {
        try
        {
            var yamlPath = Path.Combine(_profilesDir, $"{profileName}.yaml");
            if (File.Exists(yamlPath))
            {
                File.Delete(yamlPath);
                return true;
            }

            yamlPath = Path.Combine(_profilesDir, $"{profileName}.yml");
            if (File.Exists(yamlPath))
            {
                File.Delete(yamlPath);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除配置文件 {Profile} 失败", profileName);
            return false;
        }
    }
}