# DXP build report

Commit: 5564f365b75af75fb65c1222eab1d2d431f071ef
Run: https://github.com/jp-raub/sem/actions/runs/38167301073
.NET build: success
Docker build: success

Foundation only; not ready for external deployment.
  Determining projects to restore...
  Restored /home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj (in 2.26 sec).
  Sem.Web -> /home/runner/work/sem/sem/src/Sem.Web/bin/Release/net10.0/Sem.Web.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:10.77

#5 [stage-1 1/3] FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
#5 resolve mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 done
#5 ...

#6 [internal] load build context
#6 transferring context: 8.89MB 0.0s done
#6 DONE 0.1s

#5 [stage-1 1/3] FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
#5 sha256:0fa044f682cb7d93a5a90401a00c626c66f7b00b86922be9441f869eae039f80 1.58kB / 1.58kB done
#5 sha256:cc5f75342e9df50121ed06b98a4c3c1ef540823947e47b876b2bead3d4de22af 0B / 16.78MB 0.2s
#5 sha256:371202940c24d6658420d820bcc0cdb5df77c67038777ad559a9206eac8041f4 3.57kB / 3.57kB 0.2s done
#5 sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 1.08kB / 1.08kB done
#5 sha256:d61417333ceed11a9a0de53e620ea7b853639016a493a7ecd987490c017b90ef 3.13kB / 3.13kB done
#5 sha256:f2b4ef06ff01a67d4bed7657a4ae379a7d07934b7c3e743454a22a0d5275fb2b 6.29MB / 29.77MB 0.2s
#5 sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 0B / 36.60MB 0.2s
#5 sha256:cc5f75342e9df50121ed06b98a4c3c1ef540823947e47b876b2bead3d4de22af 11.53MB / 16.78MB 0.3s
#5 sha256:f2b4ef06ff01a67d4bed7657a4ae379a7d07934b7c3e743454a22a0d5275fb2b 29.77MB / 29.77MB 0.3s done
#5 sha256:ac6f29623db71ff92d8da6791126e0f01e6b58ed621a56fc46bcec31a487f9b9 0B / 154B 0.3s
#5 sha256:cc5f75342e9df50121ed06b98a4c3c1ef540823947e47b876b2bead3d4de22af 16.78MB / 16.78MB 0.3s done
#5 sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 10.49MB / 36.60MB 0.4s
#5 sha256:ac6f29623db71ff92d8da6791126e0f01e6b58ed621a56fc46bcec31a487f9b9 154B / 154B 0.3s done
#5 sha256:e3ea09d212970668f1a1699e25c84a1f256d7d0d2544f1bf9ee234c17b70f851 5.24MB / 12.74MB 0.4s
#5 sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 27.26MB / 36.60MB 0.5s
#5 sha256:e3ea09d212970668f1a1699e25c84a1f256d7d0d2544f1bf9ee234c17b70f851 12.74MB / 12.74MB 0.4s done
#5 extracting sha256:f2b4ef06ff01a67d4bed7657a4ae379a7d07934b7c3e743454a22a0d5275fb2b 0.2s
#5 sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 32.51MB / 36.60MB 0.7s
#5 sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 36.60MB / 36.60MB 0.7s done
#5 extracting sha256:cc5f75342e9df50121ed06b98a4c3c1ef540823947e47b876b2bead3d4de22af 0.5s
#5 extracting sha256:371202940c24d6658420d820bcc0cdb5df77c67038777ad559a9206eac8041f4
#5 extracting sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a
#5 extracting sha256:ac6f29623db71ff92d8da6791126e0f01e6b58ed621a56fc46bcec31a487f9b9 done
#5 extracting sha256:e3ea09d212970668f1a1699e25c84a1f256d7d0d2544f1bf9ee234c17b70f851
#5 DONE 2.9s

#7 [stage-1 2/3] WORKDIR /app
#7 DONE 0.0s

#8 [build 1/6] FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
#8 resolve mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 done
#8 sha256:0eeb52c76e35a5431ca707ad2bc75e38006a05393045d8532ae44c15d9474523 2.64kB / 2.64kB done
#8 sha256:4df5db956df7e072bd410b3f1b69b60194ab4cdd163e8d0f44bdb0d57446371d 5.64kB / 5.64kB done
#8 sha256:f2b4ef06ff01a67d4bed7657a4ae379a7d07934b7c3e743454a22a0d5275fb2b 29.77MB / 29.77MB 0.3s done
#8 sha256:371202940c24d6658420d820bcc0cdb5df77c67038777ad559a9206eac8041f4 3.57kB / 3.57kB 0.2s done
#8 sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 1.08kB / 1.08kB done
#8 sha256:cc5f75342e9df50121ed06b98a4c3c1ef540823947e47b876b2bead3d4de22af 16.78MB / 16.78MB 0.3s done
#8 sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 36.60MB / 36.60MB 0.7s done
#8 sha256:ac6f29623db71ff92d8da6791126e0f01e6b58ed621a56fc46bcec31a487f9b9 154B / 154B 0.3s done
#8 extracting sha256:f2b4ef06ff01a67d4bed7657a4ae379a7d07934b7c3e743454a22a0d5275fb2b 1.1s done
#8 sha256:e3ea09d212970668f1a1699e25c84a1f256d7d0d2544f1bf9ee234c17b70f851 12.74MB / 12.74MB 0.4s done
#8 sha256:32bc8786185914d38a44bc89ffeff78e3d7fdfec4fc046bf135b1128e4a5ff70 23.84MB / 23.84MB 0.5s done
#8 sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3 21.02MB / 21.02MB 0.6s done
#8 sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c 189.60MB / 189.60MB 1.3s done
#8 sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b 20.57MB / 20.57MB 0.8s done
#8 sha256:2c6f72fec6544b6bf7da120d6e4128b4c10856e803c8d998f72f8da51a1cdedd 499B / 499B 0.7s done
#8 extracting sha256:cc5f75342e9df50121ed06b98a4c3c1ef540823947e47b876b2bead3d4de22af 0.5s done
#8 extracting sha256:371202940c24d6658420d820bcc0cdb5df77c67038777ad559a9206eac8041f4 done
#8 extracting sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 0.5s done
#8 extracting sha256:ac6f29623db71ff92d8da6791126e0f01e6b58ed621a56fc46bcec31a487f9b9 done
#8 extracting sha256:e3ea09d212970668f1a1699e25c84a1f256d7d0d2544f1bf9ee234c17b70f851 0.2s done
#8 extracting sha256:32bc8786185914d38a44bc89ffeff78e3d7fdfec4fc046bf135b1128e4a5ff70 0.1s
#8 extracting sha256:32bc8786185914d38a44bc89ffeff78e3d7fdfec4fc046bf135b1128e4a5ff70 0.8s done
#8 extracting sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c
#8 extracting sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c 3.3s done
#8 extracting sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3
#8 extracting sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3 0.1s done
#8 extracting sha256:2c6f72fec6544b6bf7da120d6e4128b4c10856e803c8d998f72f8da51a1cdedd done
#8 extracting sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b
#8 extracting sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b 0.3s done
#8 DONE 8.7s

#9 [build 2/6] WORKDIR /source
#9 DONE 0.0s

#10 [build 3/6] COPY src/Sem.Web/Sem.Web.csproj src/Sem.Web/
#10 DONE 0.0s

#11 [build 4/6] RUN dotnet restore src/Sem.Web/Sem.Web.csproj
#11 0.754   Determining projects to restore...
#11 3.145   Restored /source/src/Sem.Web/Sem.Web.csproj (in 2.05 sec).
#11 DONE 3.3s

#12 [build 5/6] COPY src/Sem.Web/ src/Sem.Web/
#12 DONE 0.0s

#13 [build 6/6] RUN dotnet publish src/Sem.Web/Sem.Web.csproj -c Release --no-restore -o /out
#13 6.814   Sem.Web -> /source/src/Sem.Web/bin/Release/net10.0/Sem.Web.dll
#13 14.78   Sem.Web -> /out/
#13 DONE 14.9s

#14 [stage-1 3/3] COPY --from=build /out/ ./
#14 DONE 0.1s

#15 exporting to image
#15 exporting layers
#15 exporting layers 0.5s done
#15 writing image sha256:cbd747abaf2a291f9135b12ba47d0957d6c27251ab9c07dbc4f2a5837d032dc5 done
#15 naming to docker.io/library/sem-dxp:ci done
#15 DONE 0.5s
