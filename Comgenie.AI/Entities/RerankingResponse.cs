using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI.Entities
{
    // {"model":"llama-cpp","object":"list","usage":{"prompt_tokens":464,"total_tokens":464},"results":[{"index":3,"relevance_score":0.9997195601463318},{"index":1,"relevance_score":4.260051355231553e-05},{"index":4,"relevance_score":2.683498132682871e-05},{"index":0,"relevance_score":1.2388974937493913e-05},{"index":2,"relevance_score":7.430161986121675e-06}]}
    internal class RerankingResponse
    {
        public string model { get; set; }
        public RerankingUsageResponse usage { get; set; }
        public List<RerankingResultResponse> results { get; set; }

    }
    internal class RerankingUsageResponse
    {
        public int prompt_tokens { get; set; }
        public int total_tokens { get; set; }
    }
    internal class RerankingResultResponse
    {
        public int index { get; set; }
        public float relevance_score { get; set; }
    }
}
