# Profile image native runtime check

The image decoder includes native components. A host build alone does not establish that decoding works in the published runtime container.

`dotnet Sem.Web.dll --profile-image-self-test` is an exact, single-argument maintenance probe. It normalizes a built-in two-pixel PNG entirely in memory and exits before deployment settings, migrations, HTTP, database access or notification workers. It accepts no image path, URL, user identity or business-operation arguments. Failure returns nonzero and prints only the exception type, not image bytes.

The integration script runs the probe inside the disposable, network-isolated published application container after readiness. It has a 20-second process timeout and retains `verification/profile-image-native.log`. Earlier source trees without the image normalizer skip this probe. It is not production deployment or user-image acceptance; dedicated service/HTTP/browser checks remain required.

Example for an already-built application image:

```sh
docker run --rm --network none sem:check --profile-image-self-test
```
