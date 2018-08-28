﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Fabric.Metadata.FileService.Client
{
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;
    using Events;
    using FileServiceResults;
    using Newtonsoft.Json;

    public class FileServiceClient : IDisposable
    {
        public event NavigatingEventHandler Navigating;
        public event NavigatedEventHandler Navigated;

        private readonly HttpClient httpClient;
        private readonly string mdsBaseUrl;

        public FileServiceClient(string accessToken, string mdsBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new ArgumentNullException(nameof(accessToken));
            }

            if (string.IsNullOrWhiteSpace(mdsBaseUrl))
            {
                throw new ArgumentNullException(nameof(mdsBaseUrl));
            }

            this.httpClient = this.CreateHttpClient(accessToken);
            this.mdsBaseUrl = mdsBaseUrl;
        }

        public async Task<CheckFileResult> CheckFileAsync(int resourceId)
        {
            if (resourceId <= 0) throw new ArgumentOutOfRangeException(nameof(resourceId));

            var baseUri = new Uri(mdsBaseUrl);
            var fullUri = new Uri(baseUri, $"Files({resourceId})");

            var method = Convert.ToString(HttpMethod.Head);

            OnNavigating(new NavigatingEventArgs(resourceId, fullUri, method));

            var httpResponse = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, fullUri));

            OnNavigated(new NavigatedEventArgs(resourceId, method, fullUri, httpResponse.StatusCode.ToString()));

            if (httpResponse.StatusCode == HttpStatusCode.NoContent)
            {
                var headersLastModified = httpResponse.Content.Headers.LastModified;
                var headersContentMd5 = httpResponse.Content.Headers.ContentMD5;
                var contentDispositionFileName = httpResponse.Content.Headers.ContentDisposition?.FileName;

                var result = new CheckFileResult
                {
                    StatusCode = httpResponse.StatusCode,
                    LastModified = headersLastModified,
                    FileNameOnServer = contentDispositionFileName,
                    FullUri = fullUri
                };

                if (headersContentMd5 != null)
                {
                    result.HashForFileOnServer = Encoding.UTF8.GetString(headersContentMd5);
                }

                return result;
            }
            else if (httpResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // this is acceptable response if the file does not exist on the server
                return new CheckFileResult
                {
                    StatusCode = httpResponse.StatusCode,
                    FullUri = fullUri
                };
            }
            else
            {
                var content = await httpResponse.Content.ReadAsStringAsync();
                return new CheckFileResult
                {
                    StatusCode = httpResponse.StatusCode,
                    Error = content,
                    FullUri = fullUri
                };
            }
        }
        public async Task<CreateSessionResult> CreateNewUploadSessionAsync(int resourceId)
        {
            if (resourceId <= 0) throw new ArgumentOutOfRangeException(nameof(resourceId));

            var baseUri = new Uri(mdsBaseUrl);
            var fullUri = new Uri(baseUri, $"Files({resourceId})/UploadSessions");

            var method = Convert.ToString(HttpMethod.Post);

            OnNavigating(new NavigatingEventArgs(resourceId, fullUri, method));

            var form = new
            {

            };

            var httpResponse = await httpClient.PostAsync(
                fullUri,
                new StringContent(JsonConvert.SerializeObject(form),
                    Encoding.UTF8,
                    "application/json"));

            OnNavigated(new NavigatedEventArgs(resourceId, method, fullUri, httpResponse.StatusCode.ToString()));

            if (httpResponse.StatusCode == HttpStatusCode.OK)
            {
                var content = await httpResponse.Content.ReadAsStringAsync();

                var clientResponse = JsonConvert.DeserializeObject<UploadSession>(content);

                var result = new CreateSessionResult
                {
                    StatusCode = httpResponse.StatusCode,
                    FullUri = fullUri,
                    Session = clientResponse
                };

                return result;
            }
            else if (httpResponse.StatusCode == HttpStatusCode.BadRequest)
            {
                var content = await httpResponse.Content.ReadAsStringAsync();
                dynamic clientResponse = JsonConvert.DeserializeObject(content);
                var errorCode = clientResponse["ErrorCode"] != null ? Convert.ToString(clientResponse["ErrorCode"]) : null;

                return new CreateSessionResult
                {
                    StatusCode = httpResponse.StatusCode,
                    FullUri = fullUri,
                    ErrorCode = errorCode,
                    Error = content
                };
            }
            else
            {
                var content = await httpResponse.Content.ReadAsStringAsync();
                return new CreateSessionResult
                {
                    StatusCode = httpResponse.StatusCode,
                    Error = content,
                    FullUri = fullUri
                };
            }
        }
        public async Task<UploadStreamResult> UploadStreamAsync(int resourceId,
            Guid sessionId,
            Stream stream,
            FilePart filePart,
            string fileName,
            long fullFileSize,
            int filePartsCount,
            int numPartsUploaded)
        {
            if (resourceId <= 0) throw new ArgumentOutOfRangeException(nameof(resourceId));
            if (numPartsUploaded < 0) throw new ArgumentOutOfRangeException(nameof(numPartsUploaded));
            if (filePartsCount < 0) throw new ArgumentOutOfRangeException(nameof(filePartsCount));

            var baseUri = new Uri(mdsBaseUrl);
            var fullUri = new Uri(baseUri, $"Files({resourceId})/UploadSessions");

            var method = Convert.ToString(HttpMethod.Post);

            using (var requestContent = new MultipartFormDataContent())
            {
                stream.Seek(0, SeekOrigin.Begin);

                var fileContent = new StreamContent(stream);
                fileContent.Headers.ContentDisposition = new
                    ContentDispositionHeaderValue("attachment")
                {
                    FileName = fileName
                };
                fileContent.Headers.ContentRange =
                    new ContentRangeHeaderValue(filePart.Offset,
                        filePart.Offset + filePart.Size);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                fileContent.Headers.ContentMD5 = Encoding.UTF8.GetBytes(filePart.Hash);
                requestContent.Add(fileContent);

                OnNavigating(new NavigatingEventArgs(resourceId, fullUri, method));

                var httpResponse = httpClient.PutAsync(fullUri, requestContent).Result;

                OnNavigated(new NavigatedEventArgs(resourceId, method, fullUri, httpResponse.StatusCode.ToString()));

                if (httpResponse.IsSuccessStatusCode)
                {
                    return new UploadStreamResult
                    {
                        PartsUploaded = numPartsUploaded++
                    };
                }
                else
                {
                    var content = await httpResponse.Content.ReadAsStringAsync();
                    return new UploadStreamResult
                    {
                        StatusCode = httpResponse.StatusCode,
                        Error = content,
                        FullUri = fullUri
                    };
                }
            }

        }

        public void Dispose()
        {
            this.httpClient.Dispose();
        }

        private HttpClient CreateHttpClient(string accessToken)
        {
            if (accessToken == null) throw new ArgumentNullException(nameof(accessToken));

            var createHttpClient = new HttpClient();
            createHttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            createHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return createHttpClient;
        }

        private void OnNavigating(NavigatingEventArgs e)
        {
            Navigating?.Invoke(this, e);
        }
        private void OnNavigated(NavigatedEventArgs e)
        {
            Navigated?.Invoke(this, e);
        }
    }
}
