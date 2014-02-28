using System;
using System.IO;
using System.Net;
using System.Collections.Generic;
using System.Text;
using Microsoft.Phone.Controls;
using System.Windows.Navigation;
using System.Windows.Media.Imaging;
using System.IO.IsolatedStorage;
using Odnoklassniki.ServiceStructures;
using System.ComponentModel;
using System.Linq;

namespace Odnoklassniki
{
    class SDK
    {

        [DefaultValue("OK_SDK_")]
        public static string SettingsPrefix{get; set;}

        /*
         * Uris, uri templates, data templates
         */
        private const string URI_API_REQUEST = "http://api.odnoklassniki.ru/fb.do";
        private const string URI_TOKEN_REQUEST = "http://api.odnoklassniki.ru/oauth/token.do";
        private const string URI_TEMPLATE_AUTH = "http://www.odnoklassniki.ru/oauth/authorize?client_id={0}&scope={1}&response_type=code&redirect_uri={2}&layout=m";
        private const string DATA_TEMPLATE_AUTH_TOKEN_REQUEST = "code={0}&redirect_uri={1}&grant_type=authorization_code&client_id={2}&client_secret={3}";
        private const string DATA_TEMPLATE_AUTH_TOKEN_UPDATE_REQUEST = "refresh_token={0}&grant_type=refresh_token&client_id={1}&client_secret={2}";
        /*
         * End uris, uri templates, data templates
         */
        public const string ERROR_SESSION_EXPIRED = "SESSION_EXPIRED";
        public const string ERROR_NO_TOKEN_SENT_BY_SERVER = "NO_ACCESS_TOKEN_SENT_BY_SERVER";
        /*
         * if you see this text, read about errors here http://apiok.ru/wiki/pages/viewpage.action?pageId=77824003
         */
        public const string ERROR_BAD_API_REQUEST = "BAD_API_REQUEST";
        private const string SDK_EXCEPTION = "Odnoklassniki sdk exception. Please, check your app info, request correctness and internet connection. If problem persists, contact SDK developers with error and your actions description.";
        private const string PARAMETER_NAME_ACCESS_TOKEN = "access_token";
        private const string PARAMETER_NAME_REFRESH_TOKEN = "refresh_token";
        private string app_id;
        private string app_public_key;
        private string app_secret_key;
        private string redirect_url;
        private string permissions;
        private string access_token;
        private string refresh_token;
        private string code;
        private ConcurrentDictionary<HttpWebRequest, CallbackStruct> callbacks = new ConcurrentDictionary<HttpWebRequest, CallbackStruct>();
        private AuthCallbackStruct authCallback, updateCallback;

        private enum OAuthRequestType : byte { OAuthTypeAuth, OAuthTypeUpdateToken };

        public SDK(string applicationId, string applicationPublicKey, string applicationSecretKey, string redirectURL, string permissions)
        {
            this.app_id = applicationId;
            this.app_public_key = applicationPublicKey;
            this.app_secret_key = applicationSecretKey;
            this.redirect_url = redirectURL;
            this.permissions = permissions;
        }

        /**
         * Authorize the application with permissions.
         * Calls onSuccess after correct response, onError otherwise(in callbackContext thread).
         * @param browser - browser element will be used for OAuth2 authorisation.
         * @param callbackContext - PhoneApplicationPage in context of witch RequestCallback would be called. Used to make working with UI components from callbacks simplier.
         * @param onSuccess - this function will be called after success authorisation(in callbackContext thread)
         * @param onError - this function will be called after unsuccess authorisation(in callbackContext thread)
         */
        public void Authorize(WebBrowser browser, PhoneApplicationPage callbackContext, Action onSuccess, Action<Exception> onError, bool saveSession = true)
        {
            this.authCallback.onSuccess = onSuccess;
            this.authCallback.onError = onError;
            this.authCallback.callbackContext = callbackContext;
            this.authCallback.saveSession = saveSession;
            Uri uri = new Uri(String.Format(URI_TEMPLATE_AUTH, this.app_id, this.permissions, this.redirect_url), UriKind.Absolute);
            browser.Navigated += new EventHandler<NavigationEventArgs>(NavigateHandler);
            browser.Navigate(uri);
        }

        /*
         * Prepairs and sends API request.
         * Calls onSuccess after correct response, onError otherwise(in callbackContext thread).
         * @param method methodname
         * @param parameters dictionary "parameter_name":"parameter_value"
         * @param callbackContext - PhoneApplicationPage in context of witch RequestCallback would be called. Used to make working with UI components from callbacks simplier.
         * @param onSuccess - this function will be called after success authorisation(in callbackContext thread)
         * @param onError - this function will be called after unsuccess authorisation(in callbackContext thread)
         */
        public void SendRequest(string method, Dictionary<string, string> parameters, PhoneApplicationPage callbackContext, Action<string> onSuccess, Action<Exception> onError)
        {
            try
            {
                Dictionary<string, string> parametersLocal;
                if (parameters == null)
                {
                    parametersLocal = new Dictionary<string, string>();
                }
                else
                {
                    parametersLocal = new Dictionary<string, string>(parameters);
                }
                StringBuilder builder = new StringBuilder(URI_API_REQUEST).Append("?");
                parametersLocal.Add("sig", this.CalcSignature(method, parameters));
                parametersLocal.Add("application_key", this.app_public_key);
                parametersLocal.Add("method", method);
                parametersLocal.Add(PARAMETER_NAME_ACCESS_TOKEN, this.access_token);
                foreach (KeyValuePair<string, string> pair in parametersLocal)
                {
                    builder.Append(pair.Key).Append("=").Append(pair.Value).Append("&");
                }
                // removing last & added with cycle
                builder.Remove(builder.Length - 1, 1);
                HttpWebRequest request = HttpWebRequest.CreateHttp(builder.ToString());
                CallbackStruct callbackStruct;
                callbackStruct.onSuccess = onSuccess;
                callbackStruct.callbackContext = callbackContext;
                callbackStruct.onError = onError;
                callbacks.safeAdd(request, callbackStruct);
                request.BeginGetResponse(this.RequestCallback, request);
            }
            catch (Exception e)
            {
                if (onError != null)
                {
                    onError.Invoke(new Exception(SDK_EXCEPTION, e));
                }
            }
        }

        /**
         * Tries to update access_token with refresh_token.
         * Calls onSuccess after correct response, onError otherwise(in callbackContext thread).
         * @param callbackContext - PhoneApplicationPage in context of witch RequestCallback would be called. Used to make working with UI components from callbacks simplier.
         * @param onSuccess - this function will be called after success authorisation(in callbackContext thread)
         * @param onError - this function will be called after unsuccess authorisation(in callbackContext thread)
         */
        public void UpdateToken(PhoneApplicationPage callbackContext, Action onSuccess, Action<Exception> onError, bool saveSession = true)
        {
            this.updateCallback.callbackContext = callbackContext;
            this.updateCallback.onSuccess = onSuccess;
            this.updateCallback.saveSession = saveSession;
            this.updateCallback.onError = onError;
            try
            {
                BeginOAuthRequest(OAuthRequestType.OAuthTypeUpdateToken);
            }
            catch(Exception e)
            {
                if (onError != null)
                {
                    onError.Invoke(new Exception(SDK_EXCEPTION, e));
                }
            }
        }
 
        /**
         * Saves acces_token and refresh_token to application isolated storage.
         */
        public void SaveSession()
        {
            try
            {
                IsolatedStorageSettings appSettings = IsolatedStorageSettings.ApplicationSettings;
                appSettings[SettingsPrefix + PARAMETER_NAME_ACCESS_TOKEN] = access_token;
                appSettings[SettingsPrefix + PARAMETER_NAME_REFRESH_TOKEN] = refresh_token;
                appSettings.Save();
            }
            catch (IsolatedStorageException e)
            {
                throw new Exception(SDK_EXCEPTION, e);
            }
        }

        /**
         * Tries to load acces_token and refresh_token from application isolated storage.
         * This function doesn't guarantee, that tokens are correct.
         * @return true if access_tokent and refresh_token loaded from isolated storage false otherwise
         */
        public bool TryLoadSession()
        {
            IsolatedStorageSettings appSettings = IsolatedStorageSettings.ApplicationSettings;
            if (appSettings.Contains(SettingsPrefix + PARAMETER_NAME_ACCESS_TOKEN) && appSettings.Contains(SettingsPrefix + PARAMETER_NAME_REFRESH_TOKEN))
            {
                access_token = (string)appSettings[SettingsPrefix + PARAMETER_NAME_ACCESS_TOKEN];
                refresh_token = (string)appSettings[SettingsPrefix + PARAMETER_NAME_REFRESH_TOKEN];
                return access_token != null && refresh_token != null;
            }
            else
            {
                return false;
            }

        }

        /*
         * Removes access_token and refresh_token from appliction isolated storage and object.
         * You have to get new tokens usin Authorise method after calling this method.
         */
        public void ResetSession()
        {
            access_token = null;
            refresh_token = null;
            IsolatedStorageSettings appSettings = IsolatedStorageSettings.ApplicationSettings;
            appSettings.Remove(SettingsPrefix + PARAMETER_NAME_ACCESS_TOKEN);
            appSettings.Remove(SettingsPrefix + PARAMETER_NAME_REFRESH_TOKEN);
        }

        #region functions used for authorisation and updating token

        private void NavigateHandler(object sender, NavigationEventArgs e)
        {
            try
            {
                WebBrowser wb = sender as WebBrowser;
                string query = e.Uri.Query;
                if (query.IndexOf("code=") != -1)
                {
                    code = query.Substring(query.IndexOf("code=") + 5);
                    BeginOAuthRequest(OAuthRequestType.OAuthTypeAuth);
                }
                else if (query.IndexOf("error=") != -1)
                {
                    throw new Exception(query.Substring(query.IndexOf("error=") + 6));
                }
            }
            catch (Exception ex)
            {
                ProcessOAuthError(new Exception(SDK_EXCEPTION, ex), OAuthRequestType.OAuthTypeAuth);
            }
        }

        private void BeginOAuthRequest(OAuthRequestType type)
        {
            try
            {
                Uri myUri = new Uri(URI_TOKEN_REQUEST);
                HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(myUri);
                request.Method = "POST";
                request.ContentType = "application/x-www-form-urlencoded";
                request.BeginGetRequestStream(new AsyncCallback((arg) => { BeginGetOAuthResponse(arg, type); }), request);

            }
            catch (Exception e)
            {
                ProcessOAuthError(new Exception(SDK_EXCEPTION, e), type);
            }
        }

        private void BeginGetOAuthResponse(IAsyncResult result, OAuthRequestType type)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)result.AsyncState; 
                Stream postStream = request.EndGetRequestStream(result);

                string parameters = null;
                if (type == OAuthRequestType.OAuthTypeAuth)
                {
                    parameters = String.Format(DATA_TEMPLATE_AUTH_TOKEN_REQUEST, new object[] {this.code, this.redirect_url, this.app_id, this.app_secret_key});
                }
                else if (type == OAuthRequestType.OAuthTypeUpdateToken)
                {
                    parameters = String.Format(DATA_TEMPLATE_AUTH_TOKEN_UPDATE_REQUEST, this.refresh_token, this.app_id, this.app_secret_key);
                }
                byte[] byteArray = Encoding.UTF8.GetBytes(parameters);

                postStream.Write(byteArray, 0, byteArray.Length);
                postStream.Close();

                request.BeginGetResponse(new AsyncCallback((arg) => { ProcessOAuthResponse(arg, type); }), request);
            }
            catch (Exception e)
            {
                ProcessOAuthError(new Exception(SDK_EXCEPTION, e), type);
            }
        }

        private void ProcessOAuthResponse(IAsyncResult callbackResult, OAuthRequestType type)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)callbackResult.AsyncState;
                HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(callbackResult);
                using (StreamReader httpWebStreamReader = new StreamReader(response.GetResponseStream()))
                {
                    string result = httpWebStreamReader.ReadToEnd();
                    int tokenPosition = result.IndexOf(PARAMETER_NAME_ACCESS_TOKEN);
                    if(tokenPosition != 0)
                    {
                        StringBuilder builder = new StringBuilder();
                        //plus length of (access_token":")
                        tokenPosition += 15;
                        while (tokenPosition < result.Length && !result[tokenPosition].Equals( '\"'))
                        {
                            builder.Append(result[tokenPosition]);
                            tokenPosition++;
                        }
                        this.access_token = builder.ToString();
                        AuthCallbackStruct callbackStruct = updateCallback;
                        if (type == OAuthRequestType.OAuthTypeAuth)
                        {
                            builder.Clear();
                            //plus length of (refresh_token":")
                            tokenPosition = result.IndexOf(PARAMETER_NAME_REFRESH_TOKEN) + 16;
                            while (tokenPosition < result.Length && !result[tokenPosition].Equals('\"'))
                            {
                                builder.Append(result[tokenPosition]);
                                tokenPosition++;
                            }
                            this.refresh_token = builder.ToString();

                            callbackStruct = authCallback;
                        }
                        if (callbackStruct.saveSession)
                        {
                            SaveSession();
                        }
                        if (callbackStruct.callbackContext != null && callbackStruct.onSuccess != null)
                        {
                            callbackStruct.callbackContext.Dispatcher.BeginInvoke(() =>
                            {
                                callbackStruct.onSuccess.Invoke();
                            });
                        }
                    }
                    else
                    {
                        ProcessOAuthError(new Exception(ERROR_NO_TOKEN_SENT_BY_SERVER), type);
                    }
                }
            }
            catch (Exception e)
            {
                ProcessOAuthError(e, type);
            }
        }

        private void ProcessOAuthError(Exception e, OAuthRequestType type)
        {
            if (type == OAuthRequestType.OAuthTypeAuth && authCallback.onError != null && authCallback.callbackContext != null)
            {
                authCallback.callbackContext.Dispatcher.BeginInvoke(() =>
                {
                    authCallback.onError.Invoke(e);
                });
            }
            else if (type == OAuthRequestType.OAuthTypeUpdateToken && updateCallback.onError != null)
            {
                updateCallback.callbackContext.Dispatcher.BeginInvoke(() =>
                {
                    updateCallback.onError.Invoke(e);
                });
            }
        }

        #endregion

        /**
         * Callback for SendRequest function.
         * Checks for errors and calls callback for each API request.
         */
        private void RequestCallback(IAsyncResult result)
        {
            HttpWebRequest request = result.AsyncState as HttpWebRequest;
            try
            {
                WebResponse response = request.EndGetResponse(result);
                string resultText = GetUTF8TextFromWebResponse(response);
                CallbackStruct callback = callbacks.safeGet(request);
                callbacks.safeRemove(request);
                if (resultText.IndexOf("\"error_code\":102") != -1)
                {
                    if (callback.onError != null)
                    {
                        callback.onError(new Exception(ERROR_SESSION_EXPIRED));
                        return;
                    }
                }
                else if (resultText.IndexOf("\"error_code\"") != -1)
                {
                    if (callback.onError != null)
                    {
                        callback.onError(new Exception(ERROR_BAD_API_REQUEST + "  " + resultText));
                        return;
                    }
                }
                if (callback.callbackContext != null && callback.onSuccess != null)
                {
                    callback.callbackContext.Dispatcher.BeginInvoke(() =>
                    {
                        callback.onSuccess.Invoke(resultText);
                    });
                }    
            }
            catch (WebException e)
            {
                Action<Exception> onError = callbacks.safeGet(request).onError;
                callbacks.safeRemove(request);
                if (onError != null)
                {
                    onError.Invoke(e);
                }
            }
        }

        private static string GetUTF8TextFromWebResponse(WebResponse response)
        {
            StringBuilder sb = new StringBuilder();
            Byte[] buf = new byte[8192];
            Stream resStream = response.GetResponseStream();
            int count;
            do
            {
                count = resStream.Read(buf, 0, buf.Length);
                if (count != 0)
                {
                    sb.Append(Encoding.UTF8.GetString(buf, 0, count));
                }
            } while (count > 0);
            return sb.ToString();
        }

        /**
         * Calculates signature for API request with given method and parameters.
         * @param method method name
         * @param parameters dictionary "parameter_name":"parameter_value"
         */
        private string CalcSignature(string method, Dictionary<string, string> parameters)
        {
            Dictionary<string, string> parametersLocal;
            if (parameters == null)
            {
                parametersLocal = new Dictionary<string, string>();
            }
            else
            {
                parametersLocal = new Dictionary<string, string>(parameters);
            }

            parametersLocal.Add("application_key", this.app_public_key);
            parametersLocal.Add("method", method);
            StringBuilder builder = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in parametersLocal.OrderBy(item=>item.Key))
            {
                builder.Append(pair.Key).Append("=").Append(pair.Value);
            }
            string s = MD5.GetMd5String(access_token.Insert(access_token.Length, this.app_secret_key));
            return MD5.GetMd5String(builder.Append(s).ToString());
        }


    }

    class Utils
    {
        public static void downloadImageAsync(Uri imageAbsoluteUri, PhoneApplicationPage context, Action<BitmapImage> callbackOnSuccess, Action<Exception> callbackOnError)
        {
            try
            {
                WebClient wc = new WebClient();
                wc.OpenReadCompleted += new OpenReadCompletedEventHandler((s, e) =>
                {
                    if (e.Error == null && !e.Cancelled)
                    {
                        try
                        {
                            BitmapImage image = new BitmapImage();
                            image.SetSource(e.Result);
                            context.Dispatcher.BeginInvoke(() =>
                            {
                                callbackOnSuccess.Invoke(image);
                            });
                        }
                        catch (Exception ex)
                        {
                            if (callbackOnError != null)
                            {
                                callbackOnError.Invoke(ex);
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Error downloading image");
                    }
                });
                wc.OpenReadAsync(imageAbsoluteUri, wc);
            }
            catch (Exception e)
            {
                if (callbackOnError != null)
                {
                    callbackOnError.Invoke(e);
                }
            }
        }
    }
}
