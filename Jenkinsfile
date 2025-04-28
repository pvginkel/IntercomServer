library('JenkinsPipelineUtils') _

podTemplate(inheritFrom: 'jenkins-agent-large kaniko', containers: [
    containerTemplate(name: 'dotnet-sdk', image: 'mcr.microsoft.com/dotnet/sdk:9.0', command: 'sleep', args: 'infinity', alwaysPullImage: true)
]) {
    node(POD_LABEL) {
        stage('Build IntercomServer') {
            dir('IntercomServer') {
                git branch: 'main',
                    credentialsId: '5f6fbd66-b41c-405f-b107-85ba6fd97f10',
                    url: 'https://github.com/pvginkel/IntercomServer.git'

                container('dotnet-sdk') {
                    sh 'dotnet restore "./IntercomServer/IntercomServer.csproj"'
                    dir('IntercomServer') {
                        sh 'dotnet publish "./IntercomServer.csproj" -c Release -o ../publish /p:UseAppHost=false'
                    }
                }

                container('kaniko') {
                    helmCharts.kaniko([
                        "registry:5000/intercom-server:${currentBuild.number}",
                        'registry:5000/intercom-server:latest'
                    ])
                }
            }
        }
    }
}
