using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace RBDE.Dashboard;

public static class BcfExporter
{
    // BCF 2.1 issue exchange. Viewpoints are intentionally absent until a verified
    // IFC GUID and camera coordinate transform are available for each model.
    public static byte[] Create(SqliteConnection db, long? modelId = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Write(zip,"bcf.version",new XElement("Version",new XAttribute("VersionId","2.1")).ToString());
            using var query=db.CreateCommand();
            query.CommandText="SELECT i.issue_id,i.rule_id,i.requirement_id,i.parameter_name,i.message,i.actual_value,i.expected_rule,i.severity,i.lifecycle_state,i.unique_id,m.path FROM issues i JOIN models m USING(model_id) WHERE ($model IS NULL OR i.model_id=$model) AND i.evaluation_status IN ('Failed','InvalidValue') ORDER BY i.issue_id LIMIT 10000";
            query.Parameters.AddWithValue("$model",modelId is null ? DBNull.Value : modelId.Value);
            using var reader=query.ExecuteReader();
            while(reader.Read())
            {
                string S(int i)=>reader.IsDBNull(i)?"":reader.GetValue(i).ToString()??"";
                var guid=Guid.NewGuid().ToString("D");var commentId=Guid.NewGuid().ToString("D");
                var title=$"{S(1)} | {S(3)} | {Path.GetFileName(S(10))}";
                var description=$"Requirement: {S(2)}\nRule: {S(1)}\nModel: {S(10)}\nRevit UniqueId: {S(9)}\nParameter: {S(3)}\nActual: {S(5)}\nExpected: {S(6)}\n{S(4)}";
                var markup=new XElement("Markup",
                    new XElement("Topic",new XAttribute("Guid",guid),new XAttribute("TopicType","Issue"),new XAttribute("TopicStatus",S(8)=="Fixed"?"Closed":"Open"),
                        new XElement("Title",title),new XElement("CreationDate",DateTime.UtcNow.ToString("O")),new XElement("CreationAuthor","RBDE"),
                        new XElement("Priority",S(7)),new XElement("Description",description)),
                    new XElement("Comment",new XAttribute("Guid",commentId),new XElement("Date",DateTime.UtcNow.ToString("O")),new XElement("Author","RBDE"),new XElement("Comment",S(4))));
                zip.CreateEntry(guid+"/");
                Write(zip,guid+"/markup.bcf",new XDocument(new XDeclaration("1.0","UTF-8",null),markup).ToString());
            }
        }
        return memory.ToArray();
    }
    private static void Write(ZipArchive zip,string path,string value)
    {
        using var writer=new StreamWriter(zip.CreateEntry(path).Open(),new System.Text.UTF8Encoding(false));writer.Write(value);
    }
}
