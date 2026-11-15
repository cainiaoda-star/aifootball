using System.Text;

namespace AiFootBall.Sources;

/// <summary>按响应头/内容自动识别 GBK/UTF-8 的文本解码器（500.com 为 GBK）。</summary>
public static class TextDecoder
{
    static TextDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding Gbk => Encoding.GetEncoding("GBK");

    public static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                var enc = Encoding.GetEncoding(charset.Trim('"'));
                return enc.GetString(bytes);
            }
            catch (ArgumentException)
            {
                // 未知字符集，回退自动识别
            }
        }

        // 无 charset 声明：优先 UTF-8 严格校验，失败则按 GBK
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Gbk.GetString(bytes);
        }
    }

    public static string DecodeGbk(byte[] bytes) => Gbk.GetString(bytes);
}
