using Grasshopper.Kernel;
using GH_IO.Serialization;
using Rhino.Geometry;
using System;
using System.IO;
using System.Net;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GrasshopperAsyncComponent;
using System.Windows.Forms;
using Newtonsoft;
using Newtonsoft.Json.Linq;



namespace Heron
{
    public class Yelp : GH_AsyncComponent
    {
        /// <summary>
        /// Initializes a new instance of the Yelp class.
        /// </summary>
        public Yelp()
          : base("Yelp", "Yelp",
              "Search business on Yelp.",
              "Heron", "GIS API")
        {
            BaseWorker = new YelpWorker();
        }
        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.tertiary; }
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Term", "term", "Search term, for example 'food' or 'restaurant'. " +
                "The term may also be business names, such as 'Starbucks'. If term is not included the endpoint will default to " +
                "searching across businesses from a small number of popular categories.", GH_ParamAccess.item);
            pManager[0].Optional = true;

            pManager.AddTextParameter("Location", "location", "Required if either latitude or longitude is not provided. " +
                "This string indicates the geographic area to be used when searching for businesses. " +
                "Examples: 'New York City', 'NYC', '350 5th Ave, New York, NY 10118'. " +
                "Businesses returned in the response may not be strictly within the specified location.", GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddNumberParameter("Latitude", "LAT", "Required if location is not provided. Latitude of the location you want to search nearby.", GH_ParamAccess.item);
            pManager[2].Optional = true;

            pManager.AddNumberParameter("Longitude", "LON", "Required if location is not provided. Longitude of the location you want to search nearby.", GH_ParamAccess.item);
            pManager[3].Optional = true;

            pManager.AddIntegerParameter("Returns", "returns", "Number of business results to return. By default, it will return 20. " +
                "Maximum is 1000, however every 50 will count as another API call which will count against the 5,000 daily call limit.", GH_ParamAccess.item, 20);

            pManager.AddTextParameter("Yelp API Key", "key", "Yelp API key string for access to Yelp resources. " +
                "Or set an Environment Variable 'YELPAPIKEY' with your key as the string.", GH_ParamAccess.item, "");

        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Yelp JSON", "json", "Yelp's JSON response", GH_ParamAccess.item);
            pManager.AddTextParameter("URL", "Url", "URL queried", GH_ParamAccess.item);
        }

        private bool IsSortBySelected(string sortByString)
        {
            return sortByString.Equals(sortBy);
        }
        private bool IsPriceSelected(string priceString)
        {
            return priceString.Equals(price);
        }
        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            Yelp.Menu_AppendSeparator(menu);

            //base.AppendAdditionalMenuItems(menu);
            Menu_AppendItem(menu, "Cancel", (s, e) =>
            {
                RequestCancellation();
            });

            Yelp.Menu_AppendSeparator(menu);

            ///Add sort by... to menu
            ToolStripMenuItem sortByMenu = new ToolStripMenuItem("Sort by...");
            sortByMenu.ToolTipText = "Suggestion to the search algorithm that the results be sorted by one of the these modes: " +
                "best_match, rating, review_count or distance.The default is best_match.Note that specifying the sort_by is a suggestion (not strictly enforced)" +
                " to Yelp's search, which considers multiple input parameters to return the most relevant results. " +
                "For example, the rating sort is not strictly sorted by the rating value, but by an adjusted rating value that takes into account the number of ratings, " +
                "similar to a Bayesian average. This is to prevent skewing results to businesses with a single review.";

            var sortByList = new List<string> { "none", "distance", "best_match", "rating", "review_count" };
            foreach (var s in sortByList)
            {
                ToolStripMenuItem sortByName = new ToolStripMenuItem(s);
                sortByName.Checked = IsSortBySelected(s);
                sortByName.Click += SortByItemOnClick;
                sortByName.Tag = s;
                sortByMenu.DropDownItems.Add(sortByName);
            }

            menu.Items.Add(sortByMenu);

            ///Add price to menu
            ToolStripMenuItem priceMenu = new ToolStripMenuItem("Filter by price...");
            priceMenu.ToolTipText = "Pricing levels to filter the search result with: 1 = $, 2 = $$, 3 = $$$, 4 = $$$$. " +
                "The price filter can be a list of comma delimited pricing levels. For example, '1, 2, 3' will filter the results to show the ones that are $, $$, or $$$.";

            var priceList = new List<string> { "None", "$", "$$", "$$$", "$$$$" };
            foreach (var p in priceList)
            {
                ToolStripMenuItem priceName = new ToolStripMenuItem(p);
                priceName.Checked = IsPriceSelected(p);
                priceName.Click += PriceItemOnClick;
                priceName.Tag = p;
                priceMenu.DropDownItems.Add(priceName);
            }

            menu.Items.Add(priceMenu);

            base.AppendAdditionalComponentMenuItems(menu);
        }

        private void SortByItemOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            string sCode = (string)item.Tag;
            if (IsSortBySelected(sCode))
                return;

            RecordUndoEvent("SortBy");

            sortBy = sCode;
            ExpireSolution(true);
        }

        private void PriceItemOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem priceItem = sender as ToolStripMenuItem;
            if (priceItem == null)
                return;

            string pCode = (string)priceItem.Tag;
            if (IsPriceSelected(pCode))
                return;

            RecordUndoEvent("Price");

            ///Convert number of dollars signs to integer
            price = pCode;
            ExpireSolution(true);
        }


        ///Sticky parameters
        ///https://developer.rhino3d.com/api/grasshopper/html/5f6a9f31-8838-40e6-ad37-a407be8f2c15.htm
        ///

        private static string sortBy = "none";
        private static string price = "None";


        public static string SortBy
        {
            get { return sortBy; }
            set
            {
                sortBy = value;
            }
        }

        public static string Price
        {
            get { return price; }
            set
            {
                price = value;
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("SortBy", SortBy);
            writer.SetString("Price", Price);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            SortBy = reader.GetString("SortBy");
            Price = reader.GetString("Price");
            return base.Read(reader);
        }


        /////////////////////////

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                //You can add image files to your project resources and access them like this:
                // return Resources.IconForThisComponent;
                return Properties.Resources.vector;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("8a9b856a-0506-4ad6-8cce-d69cb4829529"); }
        }
    }

    public class YelpWorker : WorkerInstance
    {
        public YelpWorker() : base(null) { }

        string Term { get; set; } = string.Empty;

        string Location { get; set; } = string.Empty;

        double? Lat { get; set; }

        double? Lon { get; set; }

        int Returns { get; set; }

        string ApiKey { get; set; } = string.Empty;

        string Json { get; set; } = string.Empty;

        string Url { get; set; } = string.Empty;

        public override void DoWork(Action<string, double> ReportProgress, Action Done)
        {
            // Checking for cancellation
            if (CancellationToken.IsCancellationRequested) { return; }

            int returns = Returns;
            int offset = 0;
            int limit = 0;

            string url = @"https://api.yelp.com/v3/businesses/search?";
            if (!String.IsNullOrEmpty(Term)) { url = url + "term=" + Term; }
            if (!String.IsNullOrEmpty(Location)) { url = url + "&location=" + Location; }
            if (Lat!=0.0) { url = url + "&latitude=" + Lat; }
            if (Lon != 0.0) { url = url + "&longitude=" + Lon; }
            if (Yelp.SortBy != "" || Yelp.SortBy != "none") { url = url + "&sort_by=" + Yelp.SortBy; }
            if (Yelp.Price.Count(f => (f == '$')) > 0) { url = url + "&price=" + Yelp.Price.Count(f => (f == '$')).ToString(); }

            Url = url;

            JObject o1 = new JObject();

            ///Expire component if no Location or Lat/Lon inputs
            if (Location == "" && (Lat == null || Lon == null)) { return; }

            ///Make sure there's an API key
            if (ApiKey == "")
            {
                string hApiKey = System.Environment.GetEnvironmentVariable("YELPAPIKEY");
                if (hApiKey != null)
                {
                    ApiKey = hApiKey;
                    //Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Using Yelp API key stored in Environment Variable YELPAPIKEY.");
                }
                else
                {
                    //Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No Yelp API key is specified.  Please get a valid token from yelp.com");
                    return;
                }
            }

            ///This while loop allows paging offsets for queries as results per query are limited to 50 max
            ///Note that each time this loop runs counts against the 5,000/day query limit
            while (returns > 0)
            {
                if (CancellationToken.IsCancellationRequested) { return; }

                if (returns > 50) { limit = 50; }
                else { limit = returns; }

                string limitedUrl = url + "&limit=" + limit + "&offset=" + offset;

                var httpRequest = (HttpWebRequest)WebRequest.Create(limitedUrl);

                httpRequest.Accept = "application/json";
                httpRequest.Headers["Authorization"] = "Bearer " + ApiKey;

                var httpResponse = (HttpWebResponse)httpRequest.GetResponse();
                using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                {
                    //Json = streamReader.ReadToEnd();
                    ///Combine each json return into one
                    JObject o2 = JObject.Parse(streamReader.ReadToEnd());
                    o1.Merge(o2);
                }

                returns = returns - 50;
                offset = offset + 50;

                ReportProgress(Id, ((double)(Returns - returns) / (double)Returns));

                //Url = limitedUrl;
            }

            Json = o1.ToString();

            Done();
        }

        public override WorkerInstance Duplicate() => new YelpWorker();

        public override void GetData(IGH_DataAccess DA, GH_ComponentParamServer Params)
        {
            if (CancellationToken.IsCancellationRequested) return;

            string _term = string.Empty;
            string _location = string.Empty;
            double? _lat = null;
            double? _lon = null;
            int _returns = 0;
            string _apiKey = string.Empty;

            DA.GetData(0, ref _term);
            DA.GetData(1, ref _location);
            DA.GetData(2, ref _lat);
            DA.GetData(3, ref _lon);
            DA.GetData(4, ref _returns);
            DA.GetData(5, ref _apiKey);

            Term = _term;
            Location = _location;
            Lat = _lat;
            Lon = _lon;
            Returns = _returns;
            ApiKey = _apiKey;
        }

        public override void SetData(IGH_DataAccess DA)
        {
            if (CancellationToken.IsCancellationRequested) return;
            DA.SetData(0, Json);
            DA.SetData(1, Url);
        }

    }
}