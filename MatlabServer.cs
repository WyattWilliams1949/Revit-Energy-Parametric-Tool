using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;

namespace RevitAddin
{
    public class MatlabServer
    {
        private HttpListener _listener;
        private bool _isRunning;
        private Thread _serverThread;
        private RevitEventHandler _handler;
        private Autodesk.Revit.UI.ExternalEvent _exEvent;
        private Document _doc;
        private MatlabRevitMutator _mutator;

        // Variables gathered by Revit to present to MATLAB
        private List<SimulationElement> _activeElements;
        private Dictionary<string, TargetProperty> _variableProperties = new Dictionary<string, TargetProperty>();
        private Dictionary<string, ElementId> _variableElements = new Dictionary<string, ElementId>();

        public MatlabServer(Document doc, List<SimulationElement> activeElements, RevitEventHandler handler, Autodesk.Revit.UI.ExternalEvent exEvent)
        {
            _doc = doc;
            _activeElements = FlattenElements(activeElements);
            _handler = handler;
            _exEvent = exEvent;
            _mutator = new MatlabRevitMutator(doc);

            // Populate property mappings for mutator
            foreach (var element in _activeElements)
            {
                foreach (var prop in element.Properties)
                {
                    _variableProperties[prop.Name] = prop.Property;
                    _variableElements[prop.Name] = element.ElementId;
                }
            }
        }

        private List<SimulationElement> FlattenElements(IEnumerable<SimulationElement> elements)
        {
            var result = new List<SimulationElement>();
            foreach (var el in elements)
            {
                result.Add(el);
                if (el.SubElements != null && el.SubElements.Count > 0)
                {
                    result.AddRange(FlattenElements(el.SubElements));
                }
            }
            return result;
        }

        public void Start(int port = 8080)
        {
            if (_isRunning) return;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _listener.Start();
                _isRunning = true;
                _serverThread = new Thread(Listen);
                _serverThread.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to start server: {ex.Message}");
            }
        }

        public void Stop()
        {
            _isRunning = false;
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
        }

        private void Listen()
        {
            while (_isRunning)
            {
                try
                {
                    HttpListenerContext context = _listener.GetContext();
                    ProcessRequest(context);
                }
                catch (HttpListenerException)
                {
                    // Ignored (thrown when listener is stopped)
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Server Error: {ex.Message}");
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            string responseString = "";
            int statusCode = 200;

            try
            {
                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/elements")
                {
                    // Return the active elements as JSON
                    var elementsList = new List<object>();
                    foreach (var element in _activeElements)
                    {
                        var props = new List<object>();
                        foreach (var p in element.Properties)
                        {
                            var propDict = new Dictionary<string, object>
                            {
                                { "Name", p.Name },
                                { "Property", p.Property.ToString() }
                            };
                            
                            if (p.Property == TargetProperty.RevitType)
                            {
                                propDict["AvailableRevitTypes"] = p.AvailableRevitTypes;
                            }
                            props.Add(propDict);
                        }

                        elementsList.Add(new
                        {
                            ElementName = element.ElementName,
                            Category = element.Category,
                            ElementId = element.ElementId.Value,
                            Properties = props
                        });
                    }

                    responseString = JsonSerializer.Serialize(elementsList);
                    response.ContentType = "application/json";
                }
                else if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/wall-rvalues")
                {
                    // Returns { "varName": rValueSI } for all active RevitType wall variables.
                    // Used by MATLAB to patch gbXML construction R-values after export.
                    var tcs2 = new TaskCompletionSource<bool>();
                    Dictionary<string, double> rValues = null;
                    EventHandler completionHandler2 = null;
                    completionHandler2 = (s, e) =>
                    {
                        _handler.ActionCompleted -= completionHandler2;
                        tcs2.TrySetResult(true);
                    };
                    _handler.ActionCompleted += completionHandler2;

                    _handler.CurrentAction = (app) =>
                    {
                        rValues = _mutator.GetEnvelopeRValues(_variableProperties, _variableElements);
                    };
                    _exEvent.Raise();
                    tcs2.Task.Wait();

                    responseString = JsonSerializer.Serialize(rValues ?? new Dictionary<string, double>());
                    response.ContentType = "application/json";
                }
                else if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/envelope-rvalue-by-name")
                {
                    // Returns { "rValueSI": X } for a wall type looked up by display name.
                    // Query param: ?typeName=<url-encoded wall type name>
                    // Revit thread lookup — safe even after RevertChanges because WallType still exists.
                    string typeName = request.QueryString["typeName"] ?? "";
                    var tcsRvn = new TaskCompletionSource<bool>();
                    double rValueSI = 0.0;
                    EventHandler completionHandlerRvn = null;
                    completionHandlerRvn = (s, e) =>
                    {
                        _handler.ActionCompleted -= completionHandlerRvn;
                        tcsRvn.TrySetResult(true);
                    };
                    _handler.ActionCompleted += completionHandlerRvn;

                    _handler.CurrentAction = (app) =>
                    {
                        rValueSI = _mutator.GetRValueByName(typeName);
                    };
                    _exEvent.Raise();
                    tcsRvn.Task.Wait();

                    responseString = JsonSerializer.Serialize(new { rValueSI = rValueSI });
                    response.ContentType = "application/json";
                }
                else if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/simulate")
                {
                    // Expect JSON payload: { "simFolder": "C:/path/to/folder", "scenario": { "VarName": "Value" } }
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                        string body = reader.ReadToEnd();
                        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

                        string simFolder = payload["simFolder"].GetString();
                        var scenario = JsonSerializer.Deserialize<Dictionary<string, object>>(payload["scenario"].GetRawText());

                        // Dispatch to Revit UI thread
                        var tcs = new TaskCompletionSource<bool>();
                        EventHandler completionHandler = null;
                        completionHandler = (s, e) => {
                            _handler.ActionCompleted -= completionHandler;
                            tcs.TrySetResult(true);
                        };
                        _handler.ActionCompleted += completionHandler;
                        
                        _handler.CurrentAction = (app) => {
                            _mutator.ApplyModifications(scenario, _variableProperties, _variableElements);
                            _mutator.ExportGbXml(simFolder);
                        };
                        _exEvent.Raise();
                        
                        // Wait for Revit thread to finish
                        tcs.Task.Wait();

                        responseString = JsonSerializer.Serialize(new { success = true, message = "Model updated and exported." });
                        response.ContentType = "application/json";
                    }
                }
                else if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/revert")
                {
                    // Revert changes
                    var tcs = new TaskCompletionSource<bool>();
                    EventHandler completionHandler = null;
                    completionHandler = (s, e) => {
                        _handler.ActionCompleted -= completionHandler;
                        tcs.TrySetResult(true);
                    };
                    _handler.ActionCompleted += completionHandler;
                    
                    _handler.CurrentAction = (app) => {
                        _mutator.RevertRevitChanges();
                    };
                    _exEvent.Raise();
                    
                    tcs.Task.Wait();

                    responseString = JsonSerializer.Serialize(new { success = true, message = "Reverted successfully." });
                    response.ContentType = "application/json";
                }
                else
                {
                    statusCode = 404;
                    responseString = "Not Found";
                }
            }
            catch (Exception ex)
            {
                statusCode = 500;
                responseString = JsonSerializer.Serialize(new { success = false, message = ex.Message });
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.StatusCode = statusCode;
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
    }
}
