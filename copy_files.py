import os
import shutil
import sys

def copy_files(source_dir, dest_dir):
    try:
        if not os.path.exists(source_dir):
            print(f"Error: Source '{source_dir}' not found.")
            sys.exit(1)

        if not os.path.exists(dest_dir):
            os.makedirs(dest_dir)
            print(f"Created destination: {dest_dir}")

        files = os.listdir(source_dir)
        for file_name in files:
            source_path = os.path.join(source_dir, file_name)
            dest_path = os.path.join(dest_dir, file_name)
            if os.path.isfile(source_path):
                shutil.copy2(source_path, dest_path)
                print(f"Copied: {file_name}")

    except Exception as e:
        print(f"Failed: {str(e)}")
        sys.exit(1)

if __name__ == "__main__":
    # Rundeck will pass arguments in order: script.py [source] [dest]
    if len(sys.argv) < 3:
        print("Usage: python copy_files.py <source> <dest>")
        sys.exit(1)
   
# This line strips out any accidental quotes passed by the shell
src = sys.argv[1].replace('"', '').replace("'", "")
dest = sys.argv[2].replace('"', '').replace("'", "")
copy_files(src, dest)