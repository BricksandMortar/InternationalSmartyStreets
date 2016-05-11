using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;

using Rock;
using Rock.Address;
using Rock.Attribute;
using Rock.Web.Cache;

using RestSharp;
using Newtonsoft.Json.Linq;

namespace com.bricksandmortar.InternationalSmartyStreets.Address
{
    /// <summary>
    /// The standardization/geocoding service from <a href"https://smartystreets.com/">SmartyStreets</a>
    /// </summary>
    [Description( "International Address verification service from SmartyStreets" )]
    [Export( typeof( VerificationComponent ) )]
    [ExportMetadata( "ComponentName", "Smarty Streets" )]
    [TextField( "Auth ID", "The Smarty Streets Authorization ID", true, "", "", 2 )]
    [TextField( "Auth Token", "The Smarty Streets Authorization Token", true, "", "", 3 )]
    [ValueListField( "Acceptable Standardisation Precisions", "", true, "", "", "", "AdministrativeArea:Administrative Area, Locality:Locality, Thoroughfare:Thoroughfare, Premise:Premise, DeliveryPoint:Delivery Point", Key = "addPrecision" )]
    [ValueListField( "Acceptable Geocoding Precisions", "", true, "", "", "", "AdministrativeArea:Administrative Area, Locality:Locality, Thoroughfare:Thoroughfare, Premise:Premise, DeliveryPoint:Delivery Point", Key = "geoPrecision" )]
    [DefinedValueField(Rock.SystemGuid.DefinedType.LOCATION_COUNTRIES, "Blacklisted Countries", "A list of countries that SmartyStreets should not try to verify.", false, true, key:"blacklist")]
    [BooleanField("Default to US", "If checked if no country is specified then it will default to querying as if the address is a US based address", true, key:"default")]
    public class SmartyStreets : VerificationComponent
    {
        /// <summary>
        /// Standardizes and Geocodes an address using Smarty Streets International
        /// </summary>
        /// <param name="location">The location.</param>
        /// <param name="reVerify">Should location be reverified even if it has already been successfully verified</param>
        /// <param name="result">The result code unique to the service.</param>
        /// <returns>
        /// True/False value of whether the verification was successfull or not
        /// </returns>
        public override bool VerifyLocation( Rock.Model.Location location, bool reVerify, out string result )
        {
            bool verified = false;
            result = string.Empty;

            // Only verify if location is valid, has not been locked, and
            // has either never been attempted or last attempt was in last 30 secs (prev active service failed) or reverifying
            if ( InitialCheck(location, reVerify))

            {
                RestClient client;
                IRestRequest request;
                if (location.Country != "US" || string.IsNullOrEmpty(location.Country) && !GetAttributeValue("default").AsBoolean())
                {

                    client = new RestClient("https://international-street.api.smartystreets.com/verify");
                    request = BuildInternationalRequest(location);
                }
                else
                {
                    client = new RestClient("https://api.smartystreets.com/street-address");
                    request = BuildUSRequest(location);
                }
                
                var response = client.Execute( request );

                if ( response.StatusCode == HttpStatusCode.OK )
                {
                    JArray candidates = JArray.Parse(response.Content);
                    if (candidates.Children().Any())
                    {
                        var candidate = candidates.Children().FirstOrDefault();
                        string addressPrecision = candidate["analysis"]["address_precision"].ToString();
                        var acceptableAddPrecision = GetAttributeValues("addPrecision");
                        if (candidate["analysis"]["verification_status"].ToString() == "verified" && (addressPrecision.Equals(acceptableAddPrecision.Any())))
                        {
                            //International verification
                            if (client.BaseUrl.ToString().Contains("international"))
                            {
                                string addressTwo = candidate["address2"].ToString();
                                string city = candidate["components"]["dependent_locality"].ToString();
                                location.Street1 = candidate["address1"].ToString();
                                location.Street2 = (addressTwo.Equals(city, StringComparison.Ordinal)) ? null : addressTwo;
                                location.City = city;
                                location.State = candidate["components"]["administrative_area"].ToString();
                                location.PostalCode = candidate["components"]["postal_code"].ToString(); ;
                            }

                            //US verificiation
                            else
                            {
                                location.Street1 = candidate["delivery_line_1"].ToString();
                                location.Street2 = candidate["delivery_line_2"].ToString();
                                location.City = candidate["components"]["city_name"].ToString();
                                location.County = candidate["components"]["county_name"].ToString();
                                location.State = candidate["components"]["state_abbreviation"].ToString();
                                location.PostalCode = candidate["components"]["zipcode"] + "-" + candidate["components"]["plus4_code"].ToString();

                            }
                            location.StandardizedDateTime = RockDateTime.Now;

                        }
                        else
                        {
                            verified = false;
                        }

                        string geocodePrecision = candidate["metadata"]["geocode_precision"].ToString();
                        location.GeocodeAttemptedResult = geocodePrecision;
                        var acceptableGeoPrecision = GetAttributeValues("geoPrecision");
                        if (geocodePrecision.Equals(acceptableGeoPrecision.Any()))
                        {
                            location.SetLocationPointFromLatLong((double)candidate["metadata"]["latitude"], (double)candidate["metadata"]["longitude"]);
                            location.GeocodedDateTime = RockDateTime.Now;
                        }
                        else
                        {
                            verified = false;
                        }

                        result = string.Format("Verified: {0}; Address Precision: {1}; Geocoding Precision {2}",
                           candidate["analysis"]["verification_status"].ToString(), addressPrecision, geocodePrecision);
                    }
                    else
                    {
                        result = "No Match";
                    }
                }
                else
                {
                    result = response.StatusDescription;
                }

                location.StandardizeAttemptedServiceType = "SmartyStreets";
                location.StandardizeAttemptedDateTime = RockDateTime.Now;

                location.GeocodeAttemptedServiceType = "SmartyStreets";
                location.GeocodeAttemptedDateTime = RockDateTime.Now;

            }

            return verified;
        }

        private bool InitialCheck(Rock.Model.Location location, bool reVerify)
        {
            if (!(location != null &&
                !(location.IsGeoPointLocked ?? false) &&
                (
                    !location.GeocodeAttemptedDateTime.HasValue ||
                    location.GeocodeAttemptedDateTime.Value.CompareTo(RockDateTime.Now.AddSeconds(-30)) > 0 ||
                    reVerify
                )))
            {
                return false;
            }

            var blacklist = GetAttributeValue("blacklist");
            List<Guid> blacklistGuids = blacklist.Split(',').AsGuidList();
            foreach (var guid in blacklistGuids)
            {
                if (DefinedValueCache.Read(guid).Value.Equals(location.Country))
                {
                    return false;
                }
            }
            return true;
        }

        private IRestRequest BuildInternationalRequest( Rock.Model.Location location )
        {
            var request = BuildGenericRequest();
            if (!string.IsNullOrEmpty(location.Street1))
                request.AddParameter("address1", location.Street1);
            if (!string.IsNullOrEmpty(location.Street2))
                request.AddParameter("address2", location.Street2);
            if (!string.IsNullOrEmpty(location.City))
                request.AddParameter("locality", location.City);
            if (!string.IsNullOrEmpty(location.State))
                request.AddParameter("administrative_area", location.State);
            if (!string.IsNullOrEmpty(location.PostalCode))
                request.AddParameter("postal_code", location.PostalCode);
            request.AddParameter("country", location.Country);
            return request;
        }
        private IRestRequest BuildUSRequest(Rock.Model.Location location)
        {
            var request = BuildGenericRequest();
            if (!string.IsNullOrEmpty(location.Street1) || !string.IsNullOrEmpty(location.Street2))
                request.AddParameter("street", location.Street1 + location.Street2);
            if (!string.IsNullOrEmpty(location.City))
                request.AddParameter("city", location.City);
            if (!string.IsNullOrEmpty(location.State))
                request.AddParameter("state", location.State);
            if (!string.IsNullOrEmpty(location.PostalCode))
                request.AddParameter("zipcode", location.PostalCode);
            return request;
        }

        private IRestRequest BuildGenericRequest()
        {
            var request = new RestRequest(Method.GET);
            request.RequestFormat = DataFormat.Json;
            request.AddHeader("Accept", "application/json");
            request.AddParameter("auth-id", GetAttributeValue("AuthID"));
            request.AddParameter("auth-token", GetAttributeValue("AuthToken"));
            return request;
        }


    }
}
