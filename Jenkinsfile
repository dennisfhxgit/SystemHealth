pipeline {
  agent any

  environment {
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
  }

  stages {
    stage('Install') {
      steps {
        bat 'npm ci'
      }
    }

    stage('Frontend') {
      steps {
        bat 'npm run build'
        bat 'npm run test'
      }
    }

    stage('Backend') {
      steps {
        bat 'dotnet build SystemHealth.sln -c Release'
        bat 'dotnet publish SystemHealth.Api\\SystemHealth.Api.csproj -c Release -o _publish'
      }
    }
  }
}
