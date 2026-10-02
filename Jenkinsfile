// Publishes IntercomServer with the .NET 9 SDK, builds the intercom-server image over that output,
// and pins it into IntercomDeploy, which Argo CD syncs to prd.
//
// Controller config:
//   - Job: Firmware/IntercomServer
//   - SCM: pvginkel/IntercomServer, branch main
//   - Script Path: Jenkinsfile

library identifier: 'JenkinsPipelineUtils', changelog: false

pipeline {
    agent {
        kubernetes {
            inheritFrom 'jenkins-agent-large kaniko'
            yamlMergeStrategy merge()
            yaml podYaml(templates: ['k8s'], images: [[image: 'mcr.microsoft.com/dotnet/sdk:9.0', name: 'dotnet-sdk']])
        }
    }

    options {
        disableConcurrentBuilds(abortPrevious: true)
        skipDefaultCheckout()
        timeout(time: 60, unit: 'MINUTES')
        timestamps()
    }

    triggers {
        githubPush()
    }

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Build intercom-server image') {
            steps {
                container('dotnet-sdk') {
                    sh 'dotnet restore IntercomServer/IntercomServer.csproj'
                    sh 'dotnet publish IntercomServer/IntercomServer.csproj -c Release -o publish /p:UseAppHost=false'
                }

                container('kaniko') {
                    script {
                        helmCharts.kaniko2(destinations: [
                            "registry:5000/intercom-server:${currentBuild.number}",
                            'registry:5000/intercom-server:latest',
                        ])
                    }
                }
            }
        }

        stage('Write image pins') {
            steps {
                container('k8s') {
                    script {
                        cicd.writeVersionPins(repo: 'pvginkel/IntercomDeploy', pins: [
                            'config/prd/values.yaml': ['images.intercomServer': ":${currentBuild.number}"],
                        ])
                    }
                }
            }
        }
    }
}
