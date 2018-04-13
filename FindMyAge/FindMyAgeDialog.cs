using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Connector;
using Microsoft.ProjectOxford.Face;
using FacesAPI = Microsoft.ProjectOxford.Face.Contract;
using Microsoft.ProjectOxford.Vision;
using VisionAPI = Microsoft.ProjectOxford.Vision.Contract;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace FindMyAge
{
    // For more information about this template visit http://aka.ms/azurebots-csharp-basic
    [Serializable]
    public class FindMyAgeDialog : IDialog<object>
    {
        protected int count = 1;

        public Task StartAsync(IDialogContext context)
        {
            try
            {
                context.Wait(MessageReceivedAsync);
            }
            catch (OperationCanceledException error)
            {
                return Task.FromCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                return Task.FromException(error);
            }

            return Task.CompletedTask;
        }

        public virtual async Task MessageReceivedAsync(IDialogContext context, IAwaitable<IMessageActivity> argument)
        {
            var message = await argument;
            if (message.Attachments.Count == 0)
            {
                await context.PostAsync("Send me a photo. Make sure you look nice! :)");
                context.Wait(GetUserImageAync);
            }
            else
            {
                await AnalyzeThis(context, message);
            }
        }

        private async Task GetUserImageAync(IDialogContext context, IAwaitable<IMessageActivity> result)
        {
            var message = await result;
            if (message.Attachments.Count == 0)
            {
                await context.PostAsync("You really should send a photo. Take a selfie if you don't have one.");
                context.Wait(GetUserImageAync);
            }
            else
            {
                await AnalyzeThis(context, message);
            }
        }

        private async Task AnalyzeThis(IDialogContext context, IMessageActivity message)
        {
            await context.PostAsync("Analyzing your image...");
            await UploadProcess(context, message);
            context.Wait(MessageReceivedAsync);
        }

        private async Task UploadProcess(IDialogContext context, IMessageActivity message)
        {
            var fileNames = await UploadImage(message);
            foreach (var file in fileNames)
            {
                await context.PostAsync("Still analyzing...");
                await SendFaceReply(context, file);
                await SendDescriptionReply(context, file);
            }
        }

        private async Task SendFaceReply(IDialogContext context, Uri file)
        {
            await context.PostAsync("This is taking a while...");
            var faces = await DetectAgeInImage(file);
            if (faces.Length > 0)
            {
                int fontSize = 75;
                if (faces.Length > 3 && faces.Length < 5)
                    fontSize = 36;
                else if (faces.Length >= 5)
                    fontSize = 24;
                    
                WebClient webClient = new WebClient();
                using (var fs = webClient.OpenRead(file.AbsoluteUri))
                {
                    MemoryStream outputStream = new MemoryStream();
                    using (Image maybeFace = Image.FromStream(fs, true))
                    {
                        using (Graphics g = Graphics.FromImage(maybeFace))
                        {
                            Pen yellowPen = new Pen(System.Drawing.Color.Yellow, 10);
                            foreach (FacesAPI.Face face in faces)
                            {
                                var faceRectangle = face.FaceRectangle;
                                g.DrawRectangle(yellowPen,
                                    faceRectangle.Left, faceRectangle.Top,
                                    faceRectangle.Width, faceRectangle.Height);
                                g.DrawString($"Age {face.FaceAttributes.Age}, {face.FaceAttributes.Gender.Substring(0, 1).ToUpper()}", new Font("Calibri", fontSize), new SolidBrush(Color.Yellow), faceRectangle.Left, faceRectangle.Top + faceRectangle.Height + 5);
                            }
                        }
                        maybeFace.Save(outputStream, ImageFormat.Jpeg);
                        outputStream.Seek(0, SeekOrigin.Begin);
                        await context.PostAsync("Guessing ages of people in this photo...");
                        var markedFace = await UploadStreamToBlob(outputStream);
                        IMessageActivity photoMessage = context.MakeMessage();
                        photoMessage.Attachments.Add(new Attachment()
                        {
                            ContentUrl = markedFace.AbsoluteUri,
                            ContentType = "image/jpeg"
                        });
                        await context.PostAsync(photoMessage);
                    }
                }
            }
        }

        private async Task SendDescriptionReply(IDialogContext context, Uri file)
        {
            await context.PostAsync("Thinking of a title...");
            var analysisResult = await DescribeImage(file);
            if (analysisResult.Description.Captions[0].Confidence < 0.8)
            {
                await context.PostAsync($"I am not very sure but this photo depicts \"{analysisResult.Description.Captions[0].Text}\"");
            }
            else
            {
                await context.PostAsync($"I think this photo depicts \"{analysisResult.Description.Captions[0].Text}\"");
            }
        }

        private async Task<List<Uri>> UploadImage(IMessageActivity message)
        {
            var connectorClient = new ConnectorClient(new Uri(message.ServiceUrl));
            var token = await (connectorClient.Credentials as MicrosoftAppCredentials).GetTokenAsync();
            List<Uri> fileNames = new List<Uri>();
            using (WebClient webClient = new WebClient())
            {
                foreach (var item in message.Attachments)
                {
                    webClient.Headers.Add("Authorization", $"Bearer {token}");

                    using (Stream stream = webClient.OpenRead(item.ContentUrl))
                    {
                        Uri fileName = await UploadStreamToBlob(stream, item.Name);
                        fileNames.Add(fileName);
                    }
                }
            }

            return fileNames;
        }

        private async Task<Uri> UploadStreamToBlob(Stream stream, string tempFileName = "test.jpeg")
        {
            string storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            CloudStorageAccount storageAccount;
            if (CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
            {
                CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
                var cloudBlobContainer = cloudBlobClient.GetContainerReference("photos");
                await cloudBlobContainer.CreateIfNotExistsAsync();

                BlobContainerPermissions permissions = new BlobContainerPermissions
                {
                    PublicAccess = BlobContainerPublicAccessType.Blob
                };
                await cloudBlobContainer.SetPermissionsAsync(permissions);
                var fileName = Guid.NewGuid().ToString() + Path.GetExtension(tempFileName);
                CloudBlockBlob cloudBlockBlob = cloudBlobContainer.GetBlockBlobReference(fileName);
                await cloudBlockBlob.UploadFromStreamAsync(stream);
                return cloudBlockBlob.Uri;
            }

            return new Uri("");
        }

        private async Task<VisionAPI.AnalysisResult> DescribeImage(Uri fileToProcess)
        {
            VisionAPI.AnalysisResult analysisResult = null;
            try
            {
                var visionClient = new VisionServiceClient(Environment.GetEnvironmentVariable("CognitiveVisionAPIKey"), Environment.GetEnvironmentVariable("CognitiveVisionAPILocation"));
                var features = new VisualFeature[] { VisualFeature.Description };
                WebClient webClient = new WebClient();

                using (var fs = webClient.OpenRead(fileToProcess.AbsoluteUri))
                {
                    analysisResult = await visionClient.AnalyzeImageAsync(fs, features);
                }

            }
            catch (ClientException ex)
            {
                throw new Exception(ex.Message);
            }

            return analysisResult;
        }

        private async Task<FacesAPI.Face[]> DetectAgeInImage(Uri fileToProcess)
        {
            FacesAPI.Face[] faces = null;
            // The list of Face attributes to return.
            IEnumerable<FaceAttributeType> faceAttributes =
                new FaceAttributeType[] { FaceAttributeType.Gender, FaceAttributeType.Age };
            IFaceServiceClient faceServiceClient = new FaceServiceClient(Environment.GetEnvironmentVariable("CognitiveFaceAPIKey"), Environment.GetEnvironmentVariable("CognitiveFaceAPILocation"));
            WebClient webClient = new WebClient();

            try
            {
                using (var fs = webClient.OpenRead(fileToProcess.AbsoluteUri))
                {
                    faces = await faceServiceClient.DetectAsync(fs, returnFaceId: true, returnFaceLandmarks: false, returnFaceAttributes: faceAttributes);
                }
            }
            catch (FaceAPIException ex)
            {
                throw new Exception(ex.ErrorMessage);
            }

            return faces;
        }
    }
}
