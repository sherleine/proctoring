import { createRoot } from "react-dom/client";
import { ExamPage } from "./pages/ExamPage";
import "./styles.css";

// No StrictMode: its double-mounting would open the camera, microphone and model twice in development.
createRoot(document.getElementById("root")!).render(<ExamPage />);
