import os
import fitz  # PyMuPDF
import sqlite3
import sqlite_vec
import struct
from sentence_transformers import SentenceTransformer

# 1. Configuration
MODEL_NAME = 'all-MiniLM-L6-v2'
DB_NAME = "manuals.db"
CHUNK_SIZE = 500  # Characters
CHUNK_OVERLAP = 100 # Overlap to keep context

model = SentenceTransformer(MODEL_NAME)

# 2. Database Setup
def setup_db():
    db = sqlite3.connect(DB_NAME)
    db.enable_load_extension(True)
    sqlite_vec.load(db)
    db.execute("CREATE VIRTUAL TABLE IF NOT EXISTS vec_manuals USING vec0(chunk_id INTEGER PRIMARY KEY, embedding FLOAT[384])")
    db.execute("CREATE TABLE IF NOT EXISTS text_metadata (id INTEGER PRIMARY KEY, content TEXT, manual_name TEXT, page_num INTEGER)")
    return db

# 3. PDF Parsing & Chunking Logic
def process_pdf(file_path):
    doc = fitz.open(file_path)
    filename = os.path.basename(file_path)
    chunks = []

    for page_num, page in enumerate(doc, start=1):
        text = page.get_text("text").replace("\n", " ").strip()
        
        # Simple Sliding Window Chunking
        for i in range(0, len(text), CHUNK_SIZE - CHUNK_OVERLAP):
            chunk = text[i:i + CHUNK_SIZE]
            if len(chunk) > 20: # Skip tiny fragments
                chunks.append({
                    "text": chunk,
                    "page": page_num,
                    "manual": filename
                })
    return chunks

# 4. Main Ingestion Loop
def ingest_folder(folder_path):
    db = setup_db()
    
    for file in os.listdir(folder_path):
        if file.endswith(".pdf"):
            print(f"📖 Processing {file}...")
            full_path = os.path.join(folder_path, file)
            chunks = process_pdf(full_path)
            
            for item in chunks:
                # Generate Embedding
                vector = model.encode(item['text'])
                
                # Insert Metadata
                cur = db.execute(
                    "INSERT INTO text_metadata (content, manual_name, page_num) VALUES (?, ?, ?)",
                    (item['text'], item['manual'], item['page'])
                )
                new_id = cur.lastrowid
                
                # Insert Vector
                db.execute(
                    "INSERT INTO vec_manuals(chunk_id, embedding) VALUES (?, ?)",
                    (new_id, struct.pack("384f", *vector))
                )
            db.commit()
    
    print(f"✅ Ingestion complete. Database '{DB_NAME}' is ready for C#.")
    db.close()

# Run it
# Create a folder named 'my_manuals' and drop your PDFs there
if __name__ == "__main__":
    if not os.path.exists("manuals"):
        os.makedirs("mmanuals")
        print("Please put your PDFs in the 'manuals' folder and run again.")
    else:
        ingest_folder("manuals")
