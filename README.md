# Code Pipeline with Local IIS Application Synchronizer

This console app was written to simplify the updating of on-premises windows IIS website via AWS Code Pipeline. The writer is aware that AWS provides a way to update on-prem machines utilizing AWS agents and pipeline configurations. This was created as mechanism to allow more local control of how and when the updates happen, as well as simplify the AWS settings and reduce the number of 3rd party installations required.

App is expected to run via a scheduled task at user-specified intervals/times observing a specified s3 bucket and path (Code Pipeline Build Artifacts) injected by user via configuration.

## Prerequisites
### AWS CLI 
- Credentials to your S3 buckets need to be configured externally 
  - Run `aws s3 ls {your bucket}` on server to test credential validity. This command should list objects from your target bucket.
- Add appsettings.{instance}.json file for each instance to be updated by the application.
- appsettings.json should contain instance-agnostic settings, for example the code-pipeline bucket for your AWS tenant

### Slack Message
See slack doc for generating Channel WebhookUrl : https://api.slack.com/messaging/webhooks


## How does it work
To execute the application supply an IIS-Application name parameter via command argument. The application will read the s3 bucket and path provided by settings for the IIS-Application and locate the latest build artifact in the path. The application will then download the artifact and extract it to a temporary folder in the directory specified via settings. The application will retain the last zip file in a temporary location (specify by `Temp` or user's temporary directory) and use the artifact id to check that the current version is already downloaded when the next execution occurs.

Once the artifact contents is extracted into the temporary folder the application will attempt to stop the application-pool specified via settings. On a successful stop, the application will rename the live folder (the one targeted by IIS) to an archive version, and the temporary artifact folder to the live folder.
Finally the application will start the application-pool to bring the site back on-line.
