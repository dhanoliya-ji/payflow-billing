terraform {
  required_version = ">= 1.6.0"
  required_providers {
    aws = { source = "hashicorp/aws", version = "~> 5.0" }
    random = { source = "hashicorp/random", version = "~> 3.6" }
  }
}
provider "aws" { region = var.aws_region }
variable "aws_region" { type = string, default = "us-east-1" }
variable "environment" { type = string, default = "dev" }
resource "random_password" "postgres" {
  length  = 32
  special = true
}
resource "aws_db_instance" "postgres" {
  identifier = "payflow-${var.environment}"
  engine = "postgres"
  engine_version = "17"
  instance_class = "db.t4g.micro"
  allocated_storage = 20
  db_name = "payflow"
  username = "payflow"
  password = random_password.postgres.result
  storage_encrypted = true
  publicly_accessible = false
  skip_final_snapshot = true
}
