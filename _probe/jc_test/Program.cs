using HtmlAgilityPack;
using System.Text;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var html = File.ReadAllText(@"D:/AiFootBall/_probe/bf_curl.html", Encoding.GetEncoding("GBK"));
var doc = new HtmlDocument();
doc.LoadHtml(html);

var allBf = doc.DocumentNode.SelectNodes("//p[@data-type='bf']") ?? Enumerable.Empty<HtmlNode>();
var p = allBf.First();

// 从 bf p 向上找 data-matchnum
var n = p;
for (int i = 0; i < 12 && n != null; i++)
{
    var mn = n.GetAttributeValue("data-matchnum", "");
    var fid = n.GetAttributeValue("data-fixtureid", "");
    Console.WriteLine($"层级{i}: <{n.Name}> class='{n.GetAttributeValue("class","")}' matchnum='{mn}' fid='{fid}'");
    if (!string.IsNullOrEmpty(mn)) { Console.WriteLine("  ^^ 找到了 matchnum=" + mn); }
    n = n.ParentNode;
}
