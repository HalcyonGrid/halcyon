#!/bin/bash

if [ -x runprebuild.sh ]; then
    ./runprebuild.sh
fi

nuget restore Halcyon.sln

msbuild /p:DefineConstants="_MONO_CLI_FLAG_" Halcyon.sln
