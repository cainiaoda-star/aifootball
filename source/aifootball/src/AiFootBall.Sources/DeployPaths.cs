namespace AiFootBall.Sources;

/// <summary>
/// 运行时数据目录定位（reviews / evidence / skills 都落在这里）。
///
/// 开发时能找到 AiFootBall.sln，就写到仓库根目录——代码和数据分开放。
/// 发布包（新服务器部署）里没有 .sln，原来那套 walk 会直接抛
/// DirectoryNotFoundException，`recommend-evidence` 和 `daily-review` 两个作业
/// 每小时/每天都会失败一次，所以这里退到「程序目录往上两级」＝部署根目录。
/// </summary>
public static class DeployPaths
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiFootBall.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        // app\api\bin\... 之类更深的结构也能退到部署根：往上两级通常就是包根。
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    }

    public static string Under(string first, params string[] rest)
    {
        var path = Path.Combine(Root(), first);
        foreach (var p in rest) path = Path.Combine(path, p);
        return path;
    }
}
