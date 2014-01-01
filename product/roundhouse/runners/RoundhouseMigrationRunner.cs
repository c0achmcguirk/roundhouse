namespace roundhouse.runners
{
    using System;
    using databases;
    using folders;
    using infrastructure;
    using infrastructure.app;
    using infrastructure.app.tokens;
    using infrastructure.extensions;
    using infrastructure.filesystem;
    using infrastructure.logging;
    using migrators;
    using resolvers;
    using Environment = environments.Environment;

    public class RoundhouseMigrationRunner : IRunner
    {
        private readonly string repository_path;
        private readonly Environment environment;
        private readonly KnownFolders known_folders;
        private readonly FileSystemAccess file_system;
        public DatabaseMigrator database_migrator { get; private set; }
        private readonly VersionResolver version_resolver;
        public bool silent { get; set; }
        public bool dropping_the_database { get; set; }
        public bool dont_create_the_database;
        private bool run_in_a_transaction;
        private readonly bool use_simple_recovery;
        private readonly ConfigurationPropertyHolder configuration;
        private const string SQL_EXTENSION = "*.sql";

        public RoundhouseMigrationRunner(
            string repository_path,
            Environment environment,
            KnownFolders known_folders,
            FileSystemAccess file_system,
            DatabaseMigrator database_migrator,
            VersionResolver version_resolver,
            bool silent,
            bool dropping_the_database,
            bool dont_create_the_database,
            bool run_in_a_transaction,
            bool use_simple_recovery,
            ConfigurationPropertyHolder configuration)
        {
            this.known_folders = known_folders;
            this.repository_path = repository_path;
            this.environment = environment;
            this.file_system = file_system;
            this.database_migrator = database_migrator;
            this.version_resolver = version_resolver;
            this.silent = silent;
            this.dropping_the_database = dropping_the_database;
            this.dont_create_the_database = dont_create_the_database;
            this.run_in_a_transaction = run_in_a_transaction;
            this.use_simple_recovery = use_simple_recovery;
            this.configuration = configuration;
        }

        public void run()
        {
            initialize_database_connections();

            log_initial_events();

            if (configuration.DryRun)
            {
                this.log_info_event_on_bound_logger("This is a dry run, nothing will be done to the database.");
                database_migrator.database.Dispose();
                WaitForKeypress();
            }

            handle_invalid_transaction_argument();
            create_change_drop_folder_and_log();

            try
            {
                this.log_action_starting();

                create_share_and_set_permissions_for_change_drop_folder();
                //database_migrator.backup_database_if_it_exists();
                remove_share_from_change_drop_folder();

                bool database_was_created = false;

                if (!dropping_the_database)
                {
                    if (!dont_create_the_database)
                    {
                        database_was_created = create_or_restore_the_database();
                    }
                    
                    if (configuration.RecoveryMode != RecoveryMode.NoChange)
                    {
                        database_migrator.set_recovery_mode(configuration.RecoveryMode == RecoveryMode.Simple);
                    }


                    database_migrator.open_connection(run_in_a_transaction);

                    log_and_run_support_tasks();

                    string new_version = version_resolver.resolve_version();
                    var version_id = log_and_run_version_the_database(new_version);

                    log_migration_scripts();
                    log_and_traverse_known_folders(version_id, new_version, database_was_created);

                    if (run_in_a_transaction)
                    {
                        database_migrator.close_connection();
                        database_migrator.open_connection(false);
                    }
                    log_and_traverse(known_folders.permissions, version_id, new_version, ConnectionType.Default);

                    log_info_event_on_bound_logger(
                        "{0}{0}{1} v{2} has kicked your database ({3})! You are now at version {4}. All changes and backups can be found at \"{5}\".",
                        System.Environment.NewLine,
                        ApplicationParameters.name,
                        VersionInformation.get_current_assembly_version(),
                        database_migrator.database.database_name,
                        new_version,
                        known_folders.change_drop.folder_full_path);
                    database_migrator.close_connection();
                }
                else
                {
                    this.drop_the_database();
                }
            }
            catch (Exception ex)
            {
                this.log_exception_and_throw(ex);
            }
            finally
            {
                database_migrator.database.Dispose();
                //copy_log_file_to_change_drop_folder();
            }
        }

        protected virtual bool create_or_restore_the_database()
        {
            if (configuration.DryRun)
            {
                log_info_event_on_bound_logger("{0}{0}-DryRun- Would have created the database using this script: {1}",
                    System.Environment.NewLine,
                    this.get_custom_create_database_script()
                    );
                return false;
            }
            else
            {
                log_info_event_on_bound_logger("{0}{0} Creating the database using this script: {1}",
                    System.Environment.NewLine,
                    this.get_custom_create_database_script()
                    );
                return database_migrator.create_or_restore_database(this.get_custom_create_database_script());
            }
        }

        protected virtual void log_info_event_on_bound_logger(string message, params object[] args)
        {
            get_bound_logger().log_an_info_event_containing(message, args);
        }

        protected virtual void WaitForKeypress()
        {
            Console.ReadLine();
        }

        protected virtual void initialize_database_connections()
        {
            this.database_migrator.initialize_connections();
        }

        protected virtual Logger get_bound_logger()
        {
            return Log.bound_to(this);
        }

        private void log_action_starting()
        {
            this.log_separation_line();
            log_info_event_on_bound_logger("Setup, Backup, Create/Restore/Drop");
            this.log_separation_line();
        }

        private void log_and_traverse_known_folders(long version_id, string new_version, bool database_was_created)
        {
            this.log_and_traverse_alter_database_scripts(version_id, new_version);
            this.log_and_traverse_after_create_database_scripts(database_was_created, version_id, new_version);
            this.log_and_traverse(this.known_folders.run_before_up, version_id, new_version, ConnectionType.Default);
            this.log_and_traverse(this.known_folders.up, version_id, new_version, ConnectionType.Default);
            this.log_and_traverse(this.known_folders.run_first_after_up, version_id, new_version, ConnectionType.Default);
            this.log_and_traverse(this.known_folders.functions, version_id, new_version, ConnectionType.Default);
            this.log_and_traverse(this.known_folders.views, version_id, new_version, ConnectionType.Default);
            this.log_and_traverse(this.known_folders.sprocs, version_id, new_version, ConnectionType.Default);
            this.log_and_traverse(this.known_folders.indexes, version_id, new_version, ConnectionType.Default);
            this.log_and_traverse(this.known_folders.run_after_other_any_time_scripts, version_id, new_version, ConnectionType.Default);
        }

        private void log_and_traverse_after_create_database_scripts(
            bool database_was_created,
            long version_id,
            string new_version)
        {
            if (database_was_created)
            {
                this.log_and_traverse(this.known_folders.run_after_create_database, version_id, new_version, ConnectionType.Default);
            }
        }

        private void log_and_traverse_alter_database_scripts(long version_id, string new_version)
        {
            this.database_migrator.open_admin_connection();
            this.log_and_traverse(this.known_folders.alter_database, version_id, new_version, ConnectionType.Admin);
            this.database_migrator.close_admin_connection();
        }

        private void log_migration_scripts()
        {
            this.log_separation_line();
            log_info_event_on_bound_logger("Migration Scripts");
            this.log_separation_line();
        }

        private long log_and_run_version_the_database(string new_version)
        {
            this.log_separation_line();
            log_info_event_on_bound_logger("Versioning");
            this.log_separation_line();
            string current_version = this.database_migrator.get_current_version(this.repository_path);
            log_info_event_on_bound_logger(
                    " Migrating {0} from version {1} to {2}.",
                    this.database_migrator.database.database_name,
                    current_version,
                    new_version);
            long version_id = this.database_migrator.version_the_database(this.repository_path, new_version);
            return version_id;
        }

        private void log_and_run_support_tasks()
        {
            this.log_separation_line();
            log_info_event_on_bound_logger("RoundhousE Structure");
            this.log_separation_line();
            this.database_migrator.run_roundhouse_support_tasks();
        }

        private void log_separation_line()
        {
            log_info_event_on_bound_logger("{0}", "=".PadRight(50, '='));
        }

        private void create_change_drop_folder_and_log()
        {
            this.create_change_drop_folder();
            log_debug_event_on_bound_logger("The change_drop (output) folder is: {0}", this.known_folders.change_drop.folder_full_path);
            log_debug_event_on_bound_logger("Using SearchAllSubdirectoriesInsteadOfTraverse execution: {0}",
                    this.configuration.SearchAllSubdirectoriesInsteadOfTraverse);
        }

        protected virtual void log_debug_event_on_bound_logger(string message, params object[] args)
        {
            this.get_bound_logger().log_a_debug_event_containing(message, args);
        }

        private void handle_invalid_transaction_argument()
        {
            if (this.run_in_a_transaction && !this.database_migrator.database.supports_ddl_transactions)
            {
                log_warning_event_on_bound_logger("You asked to run in a transaction, but this dabasetype doesn't support DDL transactions.");
                if (!this.silent)
                {
                    log_info_event_on_bound_logger("Please press enter to continue without transaction support...");
                    WaitForKeypress();
                }
                this.run_in_a_transaction = false;
            }
        }

        protected virtual void log_warning_event_on_bound_logger(string message, params object[] args)
        {
            get_bound_logger().log_a_warning_event_containing(message, args);
        }

        private void log_initial_events()
        {
            log_info_event_on_bound_logger("Running {0} v{1} against {2} - {3}.",
                    ApplicationParameters.name,
                    VersionInformation.get_current_assembly_version(),
                    this.database_migrator.database.server_name,
                    this.database_migrator.database.database_name);

            log_info_event_on_bound_logger("Looking in {0} for scripts to run.", this.known_folders.up.folder_path);

            if (!this.silent)
            {
                log_info_event_on_bound_logger("Please press enter when ready to kick...");
                WaitForKeypress();
            }
        }

        protected virtual void drop_the_database()
        {
            if (configuration.DryRun)
            {
                log_info_event_on_bound_logger(
                    "{0}{0}-DryRun-{1} would have removed database ({2}). All changes and backups would be found at \"{3}\".",
                    System.Environment.NewLine,
                    ApplicationParameters.name,
                    this.database_migrator.database.database_name,
                    this.known_folders.change_drop.folder_full_path);
            }
            else
            {
                this.database_migrator.open_admin_connection();
                this.database_migrator.delete_database();
                this.database_migrator.close_admin_connection();
                this.database_migrator.close_connection();
                log_info_event_on_bound_logger(
                    "{0}{0}{1} has removed database ({2}). All changes and backups can be found at \"{3}\".",
                    System.Environment.NewLine,
                    ApplicationParameters.name,
                    this.database_migrator.database.database_name,
                    this.known_folders.change_drop.folder_full_path);
            }
        }

        private void log_exception_and_throw(Exception ex)
        {
            this.get_bound_logger()
                .log_an_error_event_containing(
                    "{0} encountered an error.{1}{2}{3}",
                    ApplicationParameters.name,
                    this.run_in_a_transaction
                        ? " You were running in a transaction though, so the database should be in the state it was in prior to this piece running. This does not include a drop/create or any creation of a database, as those items can not run in a transaction."
                        : string.Empty,
                    System.Environment.NewLine,
                    ex.to_string());

            throw ex;
        }

        public void log_and_traverse(MigrationsFolder folder, long version_id, string new_version, ConnectionType connection_type)
        {
            log_info_event_on_bound_logger("{0}", "-".PadRight(50, '-'));

            log_info_event_on_bound_logger("Looking for {0} scripts in \"{1}\".{2}{3}",
                                                            folder.friendly_name,
                                                            folder.folder_full_path,
                                                            folder.should_run_items_in_folder_once ? " These should be one time only scripts." : string.Empty,
                                                            folder.should_run_items_in_folder_every_time ? " These scripts will run every time" : string.Empty);

            log_info_event_on_bound_logger("{0}", "-".PadRight(50, '-'));
            traverse_files_and_run_sql(folder.folder_full_path, version_id, folder, environment, new_version, connection_type);
        }

        private string get_custom_create_database_script()
        {
            if (string.IsNullOrEmpty(configuration.CreateDatabaseCustomScript))
            {
                return configuration.CreateDatabaseCustomScript;
            }

            if (file_system.file_exists(configuration.CreateDatabaseCustomScript))
            {
                return file_system.read_file_text(configuration.CreateDatabaseCustomScript);
            }

            return configuration.CreateDatabaseCustomScript;
        }

        protected virtual void create_change_drop_folder()
        {
            file_system.create_directory(known_folders.change_drop.folder_full_path);
        }

        private void create_share_and_set_permissions_for_change_drop_folder()
        {
            if (!configuration.DryRun)
            {
                //todo: implement creating share with change permissions
                //todo: implement setting Everyone to full acess to this folder
            }
        }

        private void remove_share_from_change_drop_folder()
        {
            if (!configuration.DryRun)
            {
                //todo: implement removal of the file share
            }
        }

        //todo:down story

        public void traverse_files_and_run_sql(string directory, long version_id, MigrationsFolder migration_folder, Environment migrating_environment,
                                               string repository_version, ConnectionType connection_type)
        {
            if (!file_system.directory_exists(directory)) return;

            var fileNames = configuration.SearchAllSubdirectoriesInsteadOfTraverse
                                ? file_system.get_all_file_name_strings_recurevly_in(directory, SQL_EXTENSION)
                                : file_system.get_all_file_name_strings_in(directory, SQL_EXTENSION);
            foreach (string sql_file in fileNames)
            {
                string sql_file_text = replace_tokens(get_file_text(sql_file));
                log_debug_event_on_bound_logger(" Found and running {0}.", sql_file);
                bool the_sql_ran = database_migrator.run_sql(sql_file_text, file_system.get_file_name_from(sql_file),
                                                             migration_folder.should_run_items_in_folder_once,
                                                             migration_folder.should_run_items_in_folder_every_time,
                                                             version_id, migrating_environment, repository_version, repository_path, connection_type);
                if (the_sql_ran)
                {
                    try
                    {
                        copy_to_change_drop_folder(sql_file, migration_folder);
                    }
                    catch (Exception ex)
                    {
                        log_warning_event_on_bound_logger("Unable to copy {0} to {1}. {2}{3}", sql_file, migration_folder.folder_full_path,
                                                                          System.Environment.NewLine, ex.to_string());
                    }
                }
            }

            if (configuration.SearchAllSubdirectoriesInsteadOfTraverse) return;
            foreach (string child_directory in file_system.get_all_directory_name_strings_in(directory))
            {
                traverse_files_and_run_sql(child_directory, version_id, migration_folder, migrating_environment, repository_version, connection_type);
            }
        }

        public string get_file_text(string file_location)
        {
            return file_system.read_file_text(file_location);
        }

        private string replace_tokens(string sql_text)
        {
            if (configuration.DisableTokenReplacement)
            {
                return sql_text;
            }

            return TokenReplacer.replace_tokens(configuration, sql_text);
        }

        private void copy_to_change_drop_folder(string sql_file_ran, Folder migration_folder)
        {
            if (!configuration.DisableOutput)
            {
                string destination_file = file_system.combine_paths(known_folders.change_drop.folder_full_path, "itemsRan",
                                                                    sql_file_ran.Replace(migration_folder.folder_path + "\\", string.Empty));
                file_system.verify_or_create_directory(file_system.get_directory_name_from(destination_file));
                log_debug_event_on_bound_logger("Copying file {0} to {1}.", file_system.get_file_name_from(sql_file_ran), destination_file);
                file_system.file_copy_unsafe(sql_file_ran, destination_file, true);
            }
        }
    }
}