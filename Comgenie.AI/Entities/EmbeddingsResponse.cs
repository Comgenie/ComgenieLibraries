namespace Comgenie.AI.Entities
{
    public class EmbeddingsResponse
    {
        public string model { get; set; }
        public EmbeddingsUsageResponse usage { get; set; }
        public List<EmbeddingsDataResponse> data { get; set; }
    }

    public class EmbeddingsUsageResponse
    {
        public int prompt_tokens { get; set; }
        public int total_tokens { get; set; }
    }
    public class EmbeddingsDataResponse
    {
        public float[] embedding { get; set; }
    }
    
}
