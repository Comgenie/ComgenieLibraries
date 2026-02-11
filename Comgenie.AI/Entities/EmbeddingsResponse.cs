namespace Comgenie.AI.Entities
{
    internal class EmbeddingsResponse
    {
        public string model { get; set; }
        public EmbeddingsUsageResponse usage { get; set; }
        public List<EmbeddingsDataResponse> data { get; set; }
    }

    internal class EmbeddingsUsageResponse
    {
        public int prompt_tokens { get; set; }
        public int total_tokens { get; set; }
    }
    internal class EmbeddingsDataResponse
    {
        public float[] embedding { get; set; }
    }
    
}
