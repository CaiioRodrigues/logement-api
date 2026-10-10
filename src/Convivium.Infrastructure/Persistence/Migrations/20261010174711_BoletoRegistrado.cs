using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convivium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BoletoRegistrado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "boleto_bank_account_id",
                table: "condominiums",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "boleto_certificate_cipher",
                table: "condominiums",
                type: "character varying(16384)",
                maxLength: 16384,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "boleto_certificate_expires_at",
                table: "condominiums",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "boleto_certificate_password_cipher",
                table: "condominiums",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "boleto_certificate_subject",
                table: "condominiums",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "boleto_checking_account",
                table: "condominiums",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "boleto_client_id",
                table: "condominiums",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "boleto_client_secret_cipher",
                table: "condominiums",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            // Os padroes destas duas colunas foram corrigidos a mao: o EF
            // gera 0 e "" para as linhas que ja existem, e "" nao vira enum
            // na leitura — todo condominio cadastrado deixaria de abrir.
            migrationBuilder.AddColumn<int>(
                name: "boleto_days_on_agenda",
                table: "condominiums",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "boleto_instructions",
                table: "condominiums",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "boleto_provider",
                table: "condominiums",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<bool>(
                name: "boleto_sandbox",
                table: "condominiums",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "boleto_verified_at",
                table: "condominiums",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "slip_amount",
                table: "charges",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "slip_barcode",
                table: "charges",
                type: "character varying(44)",
                maxLength: 44,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "slip_issued_at",
                table: "charges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "slip_our_number",
                table: "charges",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "slip_pix_payload",
                table: "charges",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "slip_state",
                table: "charges",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "slip_synced_at",
                table: "charges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_charges_condominium_id_slip_state",
                table: "charges",
                columns: new[] { "condominium_id", "slip_state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_charges_condominium_id_slip_state",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "boleto_bank_account_id",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_certificate_cipher",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_certificate_expires_at",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_certificate_password_cipher",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_certificate_subject",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_checking_account",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_client_id",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_client_secret_cipher",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_days_on_agenda",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_instructions",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_provider",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_sandbox",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "boleto_verified_at",
                table: "condominiums");

            migrationBuilder.DropColumn(
                name: "slip_amount",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "slip_barcode",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "slip_issued_at",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "slip_our_number",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "slip_pix_payload",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "slip_state",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "slip_synced_at",
                table: "charges");
        }
    }
}
