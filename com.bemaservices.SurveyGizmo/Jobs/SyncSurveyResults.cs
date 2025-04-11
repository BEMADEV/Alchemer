using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Quartz;
using RestSharp;

using Rock;
using Rock.Attribute;
using Rock.Data;
using Rock.Jobs;
using Rock.Model;
using Rock.Web.Cache;

namespace com.bemaservices.SurveyGizmo
{
    [TextField( "API Token", "Tha api_token.", true, "", "", 0 )]
    [TextField( "API Token Secret", "The api_token_secret", true, "", "", 1 )]
    
    [TextField( "API Base URL", "The URL used to access the survey service", true, "https://api.alchemer.com/v5/", "", 2 )]
    [IntegerField( "API Requests Per Minute", "Rate limit the number of requests per minute. Service can start returning errors if too many requests are sent within a minute.", true, 30, "", 3)]

    [IntegerField( "Days Back", "How many days to go back and search for surveys", true, 2, "", 4 )]

    [DisallowConcurrentExecution]
    public class SyncSurveyResults : RockJob
    {
        private static RestClient _client;
        private static RestRequest _request;

        /// <summary> 
        /// Empty constructor for job initialization
        /// <para>
        /// Jobs require a public empty constructor so that the
        /// scheduler can instantiate the class whenever it needs.
        /// </para>
        /// </summary>
        public SyncSurveyResults()
        {
        }

        /// <summary>
        /// Job that will process Survey Gizmo Results.
        /// 
        /// Called by the <see cref="IScheduler" /> when a
        /// <see cref="ITrigger" /> fires that is associated with
        /// the <see cref="IJob" />.
        /// </summary>
        public override void Execute()
        {
            int daysBack = GetAttributeValue( "DaysBack" ).AsIntegerOrNull() ?? 1;
            string apiToken = GetAttributeValue( "APIToken" );
            string apiTokenSecret = GetAttributeValue( "APITokenSecret" );
            string apiBaseUrl = GetAttributeValue( "APIBaseURL" ).TrimEnd( new[] { '/' } );
            int requestsPerMinute = GetAttributeValue( "APIRequestsPerMinute" ).AsIntegerOrNull() ?? 30;
            DateTime requestPerMinuteTime = RockDateTime.Now;

            if ( apiToken.IsNotNullOrWhiteSpace() && apiTokenSecret.IsNotNullOrWhiteSpace() && apiBaseUrl.IsNotNullOrWhiteSpace() )
            {
                using ( var rockContext = new RockContext() )
                {
                    // Get surveys from defined values
                    int definedTypeId = DefinedTypeCache.Get( com.bemaservices.SurveyGizmo.SystemGuid.DefinedType.SURVEY_GIZMO_SURVEYS.AsGuid() ).Id;
                    var dvSurveys = new DefinedValueService( rockContext ).GetByDefinedTypeId( definedTypeId ).ToList();

                    if ( !dvSurveys.Any() )
                    {
                        this.Result = "No surveys to sync";
                    }
                    else
                    {
                        foreach ( DefinedValue survey in dvSurveys )
                        {
                            int? surveyId = survey.Value.AsIntegerOrNull();
                            survey.LoadAttributes();

                            // Survey Complete Attribute
                            Guid? personAttributeGuid = survey.GetAttributeValue( "PersonAttribute" ).AsGuidOrNull();
                            AttributeCache personAttribute = null;
                            if ( personAttributeGuid.HasValue )
                            {
                                personAttribute = AttributeCache.Get( personAttributeGuid.Value );
                            }

                            // Question Mapping
                            Guid? questionMappingMatrixGuid = survey.GetAttributeValue( "QuestionMapping" ).AsGuidOrNull();
                            AttributeMatrix attributeMatrix = null;
                            if ( questionMappingMatrixGuid.HasValue )
                            {
                                attributeMatrix = new AttributeMatrixService( rockContext ).Get( questionMappingMatrixGuid.Value );
                            }

                            if ( surveyId.HasValue )
                            {
                                int currentPage = 1;
                                int totalPages = 2; // just setting a temporary value that will get replaced by the survey response 
                                int surveysProcessed = 0;

                                while ( currentPage < totalPages )
                                {
                                    //wait one minute between pages
                                    if( currentPage != 1 )
                                    {
                                        int milliseconds = 60000 / ( requestsPerMinute < 1 ? 1 : requestsPerMinute );
                                        System.Threading.Tasks.Task.Delay( milliseconds ).Wait();
                                    }

                                    _client = new RestClient( apiBaseUrl );

                                    // Get list of surveys Responsees
                                    _request = new RestRequest( "/survey/" + surveyId.Value.ToString() + "/surveyresponse", Method.GET );
                                    _request.AddQueryParameter( "api_token", GetAttributeValue( "APIToken" ) );
                                    _request.AddQueryParameter( "api_token_secret", GetAttributeValue( "APITokenSecret" ) );
                                    _request.AddQueryParameter( "page", currentPage.ToString() );
                                    _request.AddQueryParameter( "resultsperpage", "30" );

                                    // with status of completed
                                    _request.AddQueryParameter( "filter[field][0]", "status" );
                                    _request.AddQueryParameter( "filter[operator][0]", "=" );
                                    _request.AddQueryParameter( "filter[value][0]", "complete" );

                                    // with a rock person alias guid
                                    _request.AddQueryParameter( "filter[field][1]", "[url(\"rockpersonaliasguid\")]" );
                                    _request.AddQueryParameter( "filter[operator][1]", "IS NOT NULL" );

                                    // submitted after the days back
                                    DateTime today = RockDateTime.Today;
                                    TimeSpan days = new TimeSpan( daysBack, 0, 0, 0 );
                                    _request.AddQueryParameter( "filter[field][2]", "date_submitted" );
                                    _request.AddQueryParameter( "filter[operator][2]", ">=" );
                                    _request.AddQueryParameter( "filter[value][2]", today.Subtract( days ).ToString( "yyyy-MM-dd" ) );

                                    try
                                    {
                                        var response = _client.Execute( _request );

                                        if ( response.StatusCode == System.Net.HttpStatusCode.OK )
                                        {
                                            SurveyApiResponse surveyObject = Newtonsoft.Json.JsonConvert.DeserializeObject<SurveyApiResponse>( response.Content );

                                            currentPage = surveyObject.Page + 1;
                                            totalPages = surveyObject.TotalPages;

                                            // process survey responses
                                            foreach ( var surveyResponse in surveyObject.SurveyResults )
                                            {
                                                string urlVariable = surveyResponse.UrlVariables.Where( u => u.Key == "rockpersonaliasguid" ).Select( u => u.Value.Value ).FirstOrDefault();
                                                Guid? personAliasGuid = urlVariable.AsGuidOrNull();
                                                if ( personAliasGuid.HasValue )
                                                {
                                                    var personAlias = new PersonAliasService( rockContext ).Get( personAliasGuid.Value );
                                                    if ( personAlias != null )
                                                    {
                                                        var person = new PersonService( rockContext ).Get( personAlias.PersonId );
                                                        if ( person != null )
                                                        {
                                                            // If the survey has a "survey completed person attribute" attribute configured, save the result
                                                            if ( personAttribute != null )
                                                            {
                                                                person.LoadAttributes();
                                                                person.SetAttributeValue( personAttribute.Key, "True" );
                                                                person.SaveAttributeValue( personAttribute.Key, rockContext );
                                                            }

                                                            // If the survey has a question mapping configured, process the results
                                                            if ( attributeMatrix != null )
                                                            {
                                                                foreach ( var matrixItem in attributeMatrix.AttributeMatrixItems )
                                                                {
                                                                    matrixItem.LoadAttributes();
                                                                    int? matrixQuestionId = matrixItem.GetAttributeValue( "QuestionId" ).AsIntegerOrNull();
                                                                    Guid? matrixPersonAttributeGuid = matrixItem.GetAttributeValue( "PersonAttribute" ).AsGuidOrNull();
                                                                    AttributeCache matrixPersonAttribute = null;
                                                                    if ( matrixPersonAttributeGuid.HasValue )
                                                                    {
                                                                        matrixPersonAttribute = AttributeCache.Get( matrixPersonAttributeGuid.Value );
                                                                    }

                                                                    if ( matrixQuestionId.HasValue && matrixPersonAttribute != null )
                                                                    {
                                                                        // loop through survey answers and save results based on matching question ids
                                                                        foreach ( var questionResponse in surveyResponse.SurveyData )
                                                                        {
                                                                            if ( questionResponse.Value != null )
                                                                            {
                                                                                int questionId = questionResponse.Value.Id;
                                                                                string answer;

                                                                                // check to see is the response was a multiple choice answer.
                                                                                if ( questionResponse.Value.Options != null && questionResponse.Value.Options.Any() )
                                                                                {
                                                                                    answer = questionResponse.Value.Options
                                                                                        .Where( a => a.Value != null )
                                                                                        .Select( a => a.Value.Answer ).ToList().AsDelimited(",");
                                                                                }
                                                                                else
                                                                                {
                                                                                    answer = questionResponse.Value.Answer;
                                                                                }

                                                                                if ( questionId > 0 && questionId == matrixQuestionId.Value && answer.IsNotNullOrWhiteSpace() )
                                                                                {
                                                                                    person.LoadAttributes();
                                                                                    person.SetAttributeValue( matrixPersonAttribute.Key, answer );
                                                                                    person.SaveAttributeValue( matrixPersonAttribute.Key, rockContext );
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }

                                                surveysProcessed++;
                                                this.UpdateLastStatusMessage( "Survey responses processed: " + surveysProcessed.ToString() );
                                            }
                                        }
                                        else
                                        {
                                            throw new Exception( response.StatusCode.ToString() + ": " + response.StatusDescription );
                                        }
                                    }
                                    catch ( Exception ex )
                                    {
                                        throw new Exception( ex.Message );
                                    }
                                }

                                this.UpdateLastStatusMessage( "Total surveys processed: " + surveysProcessed.ToString() );
                            }
                            else
                            {
                                this.UpdateLastStatusMessage( "Survey could not be synced. Survey: " + surveyId.Value );
                            }
                        }
                    }
                } 
            }
            else
            {
                this.UpdateLastStatusMessage( "API key is not configured properly." );
            }
        }
    }

    /// <summary>
    /// Represents the survey response from Survey Gizmo.
    /// </summary>
    [Serializable]
    public class SurveyApiResponse
    {
        [JsonProperty( "total_count" )]
        public int TotalCount { get; set; }

        [JsonProperty( "page" )]
        public int Page { get; set; }

        [JsonProperty( "total_pages" )]
        public int TotalPages { get; set; }

        [JsonProperty( "results_per_page" )]
        public int ResultsPerPage { get; set; }

        [JsonProperty( "data" )]
        public List<SurveyResults> SurveyResults { get; set; }
    }

    /// <summary>
    /// The survey result esponse.
    /// </summary>
    [Serializable]
    public class SurveyResults
    {
        [JsonProperty( "id" )]
        public int? Id { get; set; }

        [JsonProperty( "status" )]
        public String Status { get; set; }

        [JsonProperty( "url_variables" )]
        public Dictionary<string, UrlVariable> UrlVariables { get; set; }

        [JsonProperty( "survey_data" )]
        public Dictionary<string, SurveyQuestion> SurveyData { get; set; }
    }

    /// <summary>
    /// The Url variable.
    /// </summary>
    [Serializable]
    public class UrlVariable
    {
        [JsonProperty( "key" )]
        public string Key { get; set; }

        [JsonProperty( "value" )]
        public string Value { get; set; }

        [JsonProperty( "type" )]
        public string Type { get; set; }
    }

    /// <summary>
    /// The survey question.
    /// </summary>
    [Serializable]
    public class SurveyQuestion
    {
        [JsonProperty( "id" )]
        public int Id { get; set; }

        [JsonProperty( "type" )]
        public string Type { get; set; }

        [JsonProperty( "question" )]
        public string Question { get; set; }

        [JsonProperty( "answer" )]
        public string Answer { get; set; }

        [JsonProperty( "section_id" )]
        public string SectionId { get; set; }

        [JsonProperty( "shown" )]
        public bool Shown { get; set; }

        [JsonProperty( "options" )]
        public Dictionary<string, SurveyQuestionOptions> Options { get; set; }
    }

    /// <summary>
    /// The survey question options.
    /// </summary>
    [Serializable]
    public class SurveyQuestionOptions
    {
        [JsonProperty( "id" )]
        public int Id { get; set; }

        [JsonProperty( "option" )]
        public string Option { get; set; }

        [JsonProperty( "answer" )]
        public string Answer { get; set; }
    }
}
